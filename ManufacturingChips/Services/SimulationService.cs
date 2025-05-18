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

    // Тут зберігаємо індекси ліній для UI
    private ConcurrentQueue<int> _arrivalEvents = new ConcurrentQueue<int>();

    private readonly Random _rnd = new Random();

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

        // Ініціалізуємо черги і статистики
        _queues = Enumerable.Range(0, LinesCount)
            .Select(_ => new BlockingCollection<Microchip>())
            .ToArray();

        _stats = new MachineStatistics[LinesCount][];
        _completedCount = new int[LinesCount];
        _inServiceCount = new int[LinesCount];
        _totalArrived = 0;
        _arrivalEvents = new ConcurrentQueue<int>();

        for (int i = 0; i < LinesCount; i++)
        {
            _stats[i] = Enumerable.Range(0, MachinesPerLine)
                .Select(m => new MachineStatistics
                {
                    MachineIndex      = m,
                    Utilization       = 0,
                    AverageQueueTime  = 0,
                    MaxQueueLength    = 0,
                    AverageServiceTime= 0,
                    ProcessedCount    = 0
                })
                .ToArray();
            _completedCount[i] = 0;
            _inServiceCount[i] = 0;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;

        // Автоматичний потік надходжень
        _arrivalTask = Task.Run(() => ArrivalLoop(token), token);

        // Потоки ліній
        _lineTasks = Enumerable.Range(0, LinesCount)
            .Select(idx => Task.Run(() => LineLoop(idx, token), token))
            .ToList();

        // Авто-стоп по таймауту
        Task.Run(async () =>
        {
            try { await Task.Delay(ShiftDurationSeconds * 1000, token); }
            catch { }
            Stop();
        }, token);
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts.Cancel();

        try
        {
            // Чекаємо arrivalTask + всі lineTasks
            Task.WaitAll(
                new[] { _arrivalTask }
                    .Concat(_lineTasks)
                    .ToArray()
            );
        }
        catch { /* OperationCanceled */ }

        IsRunning = false;
    }

    private void ArrivalLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // 10/2 ± 2/2 секунди
            double delay = NextUniform(2.5, 0.5);
            try { Task.Delay(TimeSpan.FromSeconds(delay), token).Wait(token); }
            catch { break; }

            // додаємо і реєструємо подію
            int idx = EnqueueNext_Internal();
            _arrivalEvents.Enqueue(idx);
        }
    }

    // Додає одну деталь у випадкову лінію, повертає індекс
    private int EnqueueNext_Internal()
    {
        var mc = new Microchip { EnqueueTime = DateTime.UtcNow };
        int lineIdx = _rnd.Next(LinesCount);
        _queues[lineIdx].Add(mc);
        Interlocked.Increment(ref _totalArrived);
        return lineIdx;
    }

    // Викликає UI через ендпоінт /EnqueueNext
    public int EnqueueNext()
    {
        if (!IsRunning) return -1;
        int idx = EnqueueNext_Internal();
        _arrivalEvents.Enqueue(idx);
        return idx;
    }

    // Повертає всі накопичені індекси ліній
    public List<int> GetArrivals()
    {
        var list = new List<int>();
        while (_arrivalEvents.TryDequeue(out int idx))
            list.Add(idx);
        return list;
    }

    private void LineLoop(int lineIdx, CancellationToken token)
    {
        var machines = _stats[lineIdx];
        var queue = _queues[lineIdx];
        var busyTimers = Enumerable.Range(0, MachinesPerLine)
            .Select(_ => new Stopwatch())
            .ToArray();

        while (!token.IsCancellationRequested)
        {
            Microchip mc;
            try { mc = queue.Take(token); }
            catch { break; }

            Interlocked.Increment(ref _inServiceCount[lineIdx]);
            mc.DequeueTime = DateTime.UtcNow;
            double qTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

            for (int m = 0; m < MachinesPerLine; m++)
            {
                var stat = machines[m];
                stat.AverageQueueTime = (stat.AverageQueueTime * stat.ProcessedCount + qTime)
                                        / (stat.ProcessedCount + 1);
                stat.MaxQueueLength = Math.Max(stat.MaxQueueLength, queue.Count);

                double[] means = { 3, 3.75, 1.75, 2.0 };
                double[] devs  = { 0.25, 0.75, 0.25, 0.75 };
                double serviceTime = NextUniform(means[m], devs[m]);

                busyTimers[m].Start();
                try { Task.Delay(TimeSpan.FromSeconds(serviceTime), token).Wait(token); }
                catch
                {
                    busyTimers[m].Stop();
                    Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                    goto After;
                }
                busyTimers[m].Stop();

                stat.AverageServiceTime = (stat.AverageServiceTime * stat.ProcessedCount + serviceTime)
                                          / (stat.ProcessedCount + 1);
                stat.ProcessedCount++;

                if (m < MachinesPerLine - 1)
                {
                    double[] tMeans = { 0.5, 0.25, 0.75 };
                    double[] tDevs  = { 0.25, 0.25, 0.25 };
                    double transferTime = NextUniform(tMeans[m], tDevs[m]);

                    try { Task.Delay(TimeSpan.FromSeconds(transferTime), token).Wait(token); }
                    catch
                    {
                        Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                        goto After;
                    }
                }
            }

            Interlocked.Increment(ref _completedCount[lineIdx]);
            Interlocked.Decrement(ref _inServiceCount[lineIdx]);

            After:
            ;
        }

        // по завершенню – розрахунок utilization
        for (int m = 0; m < MachinesPerLine; m++)
            machines[m].Utilization = busyTimers[m].Elapsed.TotalSeconds / ShiftDurationSeconds;
    }

    private double NextUniform(double mean, double dev)
        => mean - dev + _rnd.NextDouble() * (2 * dev);

    public SimulationStatsResponse GetStats()
    {
        int totalCompleted   = _completedCount.Sum();
        int totalInQueues    = _queues.Sum(q => q.Count);
        int totalInService   = _inServiceCount.Sum();
        int totalUnprocessed = totalInQueues + totalInService;

        return new SimulationStatsResponse
        {
            TotalArrived     = _totalArrived,
            TotalProcessed   = totalCompleted,
            TotalUnprocessed = totalUnprocessed,
            Stats = Enumerable.Range(0, LinesCount)
                .Select(i => new LineStatistics
                {
                    LineNumber     = i + 1,
                    CompletedCount = _completedCount[i],
                    InQueueCount   = _queues[i].Count,
                    InServiceCount = _inServiceCount[i],
                    MachineStats   = _stats[i].ToList()
                })
                .ToList()
        };
    }
}