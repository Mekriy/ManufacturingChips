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

        // Ініціалізуємо всі масиви
        _queues = Enumerable.Range(0, LinesCount)
            .Select(_ => new BlockingCollection<Microchip>())
            .ToArray();

        _stats = new MachineStatistics[LinesCount][];
        _completedCount = new int[LinesCount];
        _inServiceCount = new int[LinesCount];
        _totalArrived = 0;

        for (int i = 0; i < LinesCount; i++)
        {
            _stats[i] = Enumerable.Range(0, MachinesPerLine)
                .Select(m => new MachineStatistics {
                    MachineIndex     = m,
                    Utilization      = 0,
                    AverageQueueTime = 0,
                    MaxQueueLength   = 0,
                    AverageServiceTime = 0,
                    ProcessedCount   = 0
                })
                .ToArray();
            _completedCount[i] = 0;
            _inServiceCount[i] = 0;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;

        // Зберігаємо стандартний arrival‐потік (якщо він ще потрібен; можна прибрати)
        _arrivalTask = Task.Run(() => ArrivalLoop(token), token);

        // Запускаємо обробку ліній
        _lineTasks = new List<Task>();
        for (int line = 0; line < LinesCount; line++)
        {
            int idx = line;
            _lineTasks.Add(Task.Run(() => LineLoop(idx, token), token));
        }

        // Авто‐стоп через ShiftDurationSeconds
        Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(ShiftDurationSeconds), token); }
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
            Task.WaitAll(_lineTasks.Concat(new[]{ _arrivalTask }).ToArray());
        }
        catch { }
        IsRunning = false;
    }

    // (залишаємо ArrivalLoop, якщо хочемо мати ще й автоматичний генератор; можна не викликати його)
    private void ArrivalLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            double delaySec = NextUniform(10.0/2.0, 2.0/2.0);
            try { Task.Delay(TimeSpan.FromSeconds(delaySec), token).Wait(token); }
            catch { break; }

            // додаємо нову деталь у випадкову лінію
            EnqueueNext_Internal();
        }
    }

    // Новий метод: додаємо 1 деталь і повертаємо, на яку лінію її поставили
    public int EnqueueNext_Internal()
    {
        var mc = new Microchip { EnqueueTime = DateTime.UtcNow };
        int lineIdx = _rnd.Next(LinesCount);

        _queues[lineIdx].Add(mc);
        Interlocked.Increment(ref _totalArrived);
        return lineIdx;
    }

    // Метод, який викликає контролер
    public int EnqueueNext()
    {
        // якщо симуляція вже зупинена, повернемо -1
        if (!IsRunning) return -1;
        return EnqueueNext_Internal();
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
            double queueTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

            for (int m = 0; m < MachinesPerLine; m++)
            {
                var stat = machines[m];
                stat.AverageQueueTime = (stat.AverageQueueTime * stat.ProcessedCount + queueTime)
                                        / (stat.ProcessedCount + 1);
                stat.MaxQueueLength = Math.Max(stat.MaxQueueLength, queue.Count);

                double[] means = { 6.0, 6.5, 3.5, 4.0 };
                double[] devs = { 0.5,  1.5, 0.5, 1.5 };
                double serviceTime = NextUniform(means[m], devs[m]);

                busyTimers[m].Start();
                try { Task.Delay(TimeSpan.FromSeconds(serviceTime), token).Wait(token); }
                catch
                {
                    busyTimers[m].Stop();
                    Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                    goto AfterProcessing;
                }
                busyTimers[m].Stop();

                stat.AverageServiceTime = (stat.AverageServiceTime * stat.ProcessedCount + serviceTime)
                                          / (stat.ProcessedCount + 1);
                stat.ProcessedCount++;

                if (m < MachinesPerLine - 1)
                {
                    double[] tMeans = { 1.0, 0.5, 1.5 };
                    double[] tDevs  = { 0.5, 0.5, 0.5 };
                    double transferTime = NextUniform(tMeans[m], tDevs[m]);
                    try { Task.Delay(TimeSpan.FromSeconds(transferTime), token).Wait(token); }
                    catch
                    {
                        Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                        goto AfterProcessing;
                    }
                }
            }

            Interlocked.Increment(ref _completedCount[lineIdx]);
            Interlocked.Decrement(ref _inServiceCount[lineIdx]);

            AfterProcessing:
            ;
        }

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
            Stats = Enumerable.Range(0, LinesCount).Select(idx => new LineStatistics {
                LineNumber     = idx + 1,
                CompletedCount = _completedCount[idx],
                InQueueCount   = _queues[idx].Count,
                InServiceCount = _inServiceCount[idx],
                MachineStats   = _stats[idx].ToList()
            }).ToList()
        };
    }
}