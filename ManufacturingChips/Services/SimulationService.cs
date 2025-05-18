using System.Collections.Concurrent;
using System.Diagnostics;
using ManufacturingChips.Models;

namespace ManufacturingChips.Services;

public class SimulationService
{
    private CancellationTokenSource _cts;
    private Task _arrivalTask;
    private List<Task> _lineTasks;

    private BlockingCollection<Microchip>[] _queues;

    private MachineStatistics[][] _stats;
    private int _totalArrived;
    private int[] _completedCount;
    private int[] _inServiceCount;

    private ConcurrentQueue<int> _arrivalEvents = new();
    private ConcurrentQueue<int> _completionEvents = new();

    private bool[] _firstMachineBusy;

    private ConcurrentQueue<Microchip> _pendingQueue = new();

    private readonly Random _rnd = new();

    public int LinesCount { get; private set; }
    public int MachinesPerLine { get; private set; }
    public int ShiftDurationSeconds { get; private set; }
    public bool IsRunning { get; private set; }

    public void Start(int linesCount, int machinesPerLine, int shiftDurationSeconds)
    {
        if (IsRunning) return;
        LinesCount = linesCount;
        MachinesPerLine = machinesPerLine;
        ShiftDurationSeconds = shiftDurationSeconds;

        // Ініціалізація масивів
        _queues = Enumerable.Range(0, LinesCount)
            .Select(_ => new BlockingCollection<Microchip>())
            .ToArray();

        _stats = new MachineStatistics[LinesCount][];
        _completedCount = new int[LinesCount];
        _inServiceCount = new int[LinesCount];
        _firstMachineBusy = new bool[LinesCount];
        _totalArrived = 0;
        _pendingQueue = new ConcurrentQueue<Microchip>();
        _arrivalEvents = new ConcurrentQueue<int>();
        _completionEvents = new ConcurrentQueue<int>();

        for (int i = 0; i < LinesCount; i++)
        {
            _stats[i] = Enumerable.Range(0, MachinesPerLine)
                .Select(m => new MachineStatistics
                {
                    MachineIndex = m,
                    Utilization = 0,
                    AverageQueueTime = 0,
                    MaxQueueLength = 0,
                    AverageServiceTime = 0,
                    ProcessedCount = 0
                })
                .ToArray();
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;

        // Автоматичний arrival-потік
        _arrivalTask = Task.Run(() => ArrivalLoop(token), token);

        // Потоки ліній
        _lineTasks = Enumerable.Range(0, LinesCount)
            .Select(idx => Task.Run(() => LineLoop(idx, token), token))
            .ToList();

        // Авто-стоп після зміни
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ShiftDurationSeconds * 1000, token);
            }
            catch
            {
            }

            Stop();
        }, token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts.Cancel();
        try
        {
            Task.WaitAll(
                new[] { _arrivalTask }
                    .Concat(_lineTasks)
                    .ToArray()
            );
        }
        catch
        {
            /* OperationCanceled */
        }

        IsRunning = false;
    }

    private void ArrivalLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // 10/2 ± 2/2 сек
            double delay = NextUniform(2.5, 0.5);
            try
            {
                Task.Delay(TimeSpan.FromSeconds(delay), token).Wait(token);
            }
            catch
            {
                break;
            }

            var mc = new Microchip { EnqueueTime = DateTime.UtcNow };
            Interlocked.Increment(ref _totalArrived);
            EnqueueSmart(mc);
        }
    }

    // UI-виклик додає аналогічно
    public int EnqueueNext()
    {
        if (!IsRunning) return -1;
        var mc = new Microchip { EnqueueTime = DateTime.UtcNow };
        Interlocked.Increment(ref _totalArrived);
        return EnqueueSmart(mc);
    }

    private int EnqueueSmart(Microchip mc)
    {
        lock (_firstMachineBusy)
        {
            for (int i = 0; i < LinesCount; i++)
            {
                // якщо перший станок вільний і черга пуста
                if (!_firstMachineBusy[i] && _queues[i].Count == 0)
                {
                    DispatchToLine(mc, i);
                    return i;
                }
            }

            // інакше — чекаємо в черзі
            _pendingQueue.Enqueue(mc);
            return -1;
        }
    }

    // Додаємо mc в чергу лінії та пушимо arrivalEvent
    private void DispatchToLine(Microchip mc, int lineIdx)
    {
        _queues[lineIdx].Add(mc);
        _arrivalEvents.Enqueue(lineIdx);
    }

    // опитування UI
    public List<int> GetArrivals()
    {
        var outList = new List<int>();
        while (_arrivalEvents.TryDequeue(out int idx))
            outList.Add(idx);
        return outList;
    }

    // опитування UI для завершень
    public List<int> GetCompletions()
    {
        var outList = new List<int>();
        while (_completionEvents.TryDequeue(out int idx))
            outList.Add(idx);
        return outList;
    }

    private void LineLoop(int lineIdx, CancellationToken token)
    {
        var machines = _stats[lineIdx];
        var queue = _queues[lineIdx];
        var busy = Enumerable.Range(0, MachinesPerLine)
            .Select(_ => new Stopwatch())
            .ToArray();

        while (!token.IsCancellationRequested)
        {
            Microchip mc;
            try
            {
                mc = queue.Take(token);
            }
            catch
            {
                break;
            }

            Interlocked.Increment(ref _inServiceCount[lineIdx]);
            // перед першим станком — позначаємо як зайнятий
            _firstMachineBusy[lineIdx] = true;

            mc.DequeueTime = DateTime.UtcNow;
            double qTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

            for (int m = 0; m < MachinesPerLine; m++)
            {
                var st = machines[m];
                // оновлення статистики черги
                st.AverageQueueTime = (st.AverageQueueTime * st.ProcessedCount + qTime)
                                      / (st.ProcessedCount + 1);
                st.MaxQueueLength = Math.Max(st.MaxQueueLength, queue.Count);

                // сервісний час
                double[] means = { 3, 3.75, 1.75, 2.0 };
                double[] devs  = { 0.25, 0.75, 0.25, 0.75 };
                double srvTime = NextUniform(means[m], devs[m]);

                busy[m].Start();
                try
                {
                    Task.Delay(TimeSpan.FromSeconds(srvTime), token).Wait(token);
                }
                catch
                {
                    busy[m].Stop();
                    Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                    goto After;
                }

                busy[m].Stop();

                st.AverageServiceTime = (st.AverageServiceTime * st.ProcessedCount + srvTime)
                                        / (st.ProcessedCount + 1);
                st.ProcessedCount++;

                // передача між станками
                if (m < MachinesPerLine - 1)
                {
                    double[] tMeans = { 0.5, 0.25, 0.75 };
                    double[] tDevs  = { 0.25, 0.25, 0.25 };
                    double trTime = NextUniform(tMeans[m], tDevs[m]);

                    try
                    {
                        Task.Delay(TimeSpan.FromSeconds(trTime), token).Wait(token);
                    }
                    catch
                    {
                        Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                        goto After;
                    }
                }

                // після обробки на першому станку — звільняємо його і диспетчеризуємо очікування
                if (m == 0)
                {
                    _firstMachineBusy[lineIdx] = false;
                    // якщо хтось чекає — відразу забираємо і кидаємо в цю лінію
                    if (_pendingQueue.TryDequeue(out var pendingMc))
                        DispatchToLine(pendingMc, lineIdx);
                }
            }

            // успішне завершення всіх 4 станків
            Interlocked.Increment(ref _completedCount[lineIdx]);
            Interlocked.Decrement(ref _inServiceCount[lineIdx]);
            _completionEvents.Enqueue(lineIdx);

            After: ;
        }

        // utilization
        for (int m = 0; m < MachinesPerLine; m++)
            machines[m].Utilization = busy[m].Elapsed.TotalSeconds / ShiftDurationSeconds;
    }

    private double NextUniform(double mean, double dev)
        => mean - dev + _rnd.NextDouble() * (2 * dev);

    public SimulationStatsResponse GetStats()
    {
        if (_queues == null || _stats == null)
            return new SimulationStatsResponse
            {
                TotalArrived = 0,
                TotalProcessed = 0,
                TotalUnprocessed = 0,
                Stats = new List<LineStatistics>()
            };

        int totalCompleted = _completedCount.Sum();
        int totalInQueues = _queues.Sum(q => q.Count);
        int totalInService = _inServiceCount.Sum();
        int totalUnprocessed = totalInQueues + totalInService;

        var stats = Enumerable.Range(0, LinesCount)
            .Select(i => new LineStatistics
            {
                LineNumber = i + 1,
                CompletedCount = _completedCount[i],
                InQueueCount = _queues[i].Count,
                InServiceCount = _inServiceCount[i],
                MachineStats = _stats[i].ToList()
            }).ToList();

        return new SimulationStatsResponse
        {
            TotalArrived = _totalArrived,
            TotalProcessed = totalCompleted,
            TotalUnprocessed = totalUnprocessed,
            Stats = stats
        };
    }
}