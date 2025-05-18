using System.Collections.Concurrent;
using System.Diagnostics;
using ManufacturingChips.Hubs;
using ManufacturingChips.Interfaces;
using ManufacturingChips.Models;
using Microsoft.AspNetCore.SignalR;

namespace ManufacturingChips.Services;

public class SimulationService : ISimulationService
{
    private readonly IHubContext<SimulationHub> _hub;
    private CancellationTokenSource _cts;
    private Task _arrivalTask;
    private List<Task> _lineTasks;
    private BlockingCollection<Microchip>[] _queues;
    private MachineStatistics[][] _stats;
    private int _totalArrived;
    private int[] _completedCount;
    private int[] _inServiceCount;
    private bool[] _firstMachineBusy;
    private ConcurrentQueue<Microchip> _pendingQueue;
    private readonly Random _rnd = new();

    public int LinesCount { get; private set; }
    public int MachinesPerLine { get; private set; }
    public int ShiftDurationSeconds { get; private set; }
    public bool IsRunning { get; private set; }

    public SimulationService(IHubContext<SimulationHub> hubContext)
    {
        _hub = hubContext;
    }

    public void Start(int linesCount, int machinesPerLine, int shiftDurationSeconds)
    {
        if (IsRunning) return;

        LinesCount = linesCount;
        MachinesPerLine = machinesPerLine;
        ShiftDurationSeconds = shiftDurationSeconds;

        _queues = Enumerable.Range(0, LinesCount)
            .Select(_ => new BlockingCollection<Microchip>())
            .ToArray();
        _stats = new MachineStatistics[LinesCount][];
        _completedCount = new int[LinesCount];
        _inServiceCount = new int[LinesCount];
        _firstMachineBusy = new bool[LinesCount];
        _totalArrived = 0;
        _pendingQueue = new ConcurrentQueue<Microchip>();

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
                }).ToArray();
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;

        _arrivalTask = Task.Run(() => ArrivalLoop(token), token);
        _lineTasks = Enumerable.Range(0, LinesCount)
            .Select(idx => Task.Run(() => LineLoop(idx, token), token))
            .ToList();

        // Авто-стоп
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
            Task.WaitAll(new[] { _arrivalTask }.Concat(_lineTasks).ToArray());
        }
        catch { }
        IsRunning = false;
    }

    private void ArrivalLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            double delaySec = NextUniform(2.5, 0.5);
            try { Task.Delay(TimeSpan.FromSeconds(delaySec), token).Wait(token); }
            catch { break; }

            var mc = new Microchip
            {
                ChipId = Guid.NewGuid(),
                EnqueueTime = DateTime.UtcNow
            };
            Interlocked.Increment(ref _totalArrived);
            EnqueueSmart(mc);
        }
    }

    private void EnqueueSmart(Microchip mc)
    {
        lock (_firstMachineBusy)
        {
            for (int i = 0; i < LinesCount; i++)
            {
                if (!_firstMachineBusy[i] && _queues[i].Count == 0)
                {
                    DispatchToLine(mc, i, enqueued: false);
                    return;
                }
            }
            _pendingQueue.Enqueue(mc);
        }
    }

    private void DispatchToLine(Microchip mc, int lineIdx, bool enqueued)
    {
        var timings = new List<TimePair>();
        for (int m = 0; m < MachinesPerLine; m++)
        {
            double svc = NextUniform(
                new double[] { 3.0, 3.75, 1.75, 2.0 }[m],
                new double[] { 0.25, 0.75, 0.25, 0.75 }[m]
            );
            double trf = m < MachinesPerLine - 1
                ? NextUniform(
                    new double[] { 0.5, 0.25, 0.75 }[m],
                    new double[] { 0.25, 0.25, 0.25 }[m]
                )
                : 0;
            timings.Add(new TimePair { Service = svc, Transfer = trf });
        }

        mc.Timings = timings;
        _queues[lineIdx].Add(mc);

        // 1) базове прибуття
        _hub.Clients.All.SendAsync("OnArrival", new
        {
            lineIdx,
            chipId = mc.ChipId,
            timings,
            enqueued
        });

        // 2) якщо прийшло з очікування – сповіщаємо окремо
        if (enqueued)
        {
            _hub.Clients.All.SendAsync("OnQueueToService", new
            {
                lineIdx,
                chipId = mc.ChipId,
                timings
            });
        }
    }

    private void LineLoop(int lineIdx, CancellationToken token)
    {
        var queue = _queues[lineIdx];
        var busy = Enumerable.Range(0, MachinesPerLine)
            .Select(_ => new Stopwatch())
            .ToArray();

        while (!token.IsCancellationRequested)
        {
            Microchip mc;
            try { mc = queue.Take(token); }
            catch { break; }

            bool wasInQueue = _firstMachineBusy[lineIdx] || queue.Count > 0;
            Interlocked.Increment(ref _inServiceCount[lineIdx]);
            _firstMachineBusy[lineIdx] = true;
            mc.DequeueTime = DateTime.UtcNow;
            double qTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

            for (int m = 0; m < MachinesPerLine; m++)
            {
                var st = _stats[lineIdx][m];
                st.AverageQueueTime = (st.AverageQueueTime * st.ProcessedCount + qTime)
                                      / (st.ProcessedCount + 1);
                st.MaxQueueLength = Math.Max(st.MaxQueueLength, queue.Count);

                var tp = mc.Timings[m];
                busy[m].Start();
                try { Task.Delay(TimeSpan.FromSeconds(tp.Service), token).Wait(token); }
                catch { busy[m].Stop(); goto After; }
                busy[m].Stop();

                st.AverageServiceTime = (st.AverageServiceTime * st.ProcessedCount + tp.Service)
                                        / (st.ProcessedCount + 1);
                st.ProcessedCount++;

                // Після сервісу, якщо не остання машина – чекаємо та переходимо
                if (m < MachinesPerLine - 1)
                {
                    try { Task.Delay(TimeSpan.FromSeconds(tp.Transfer), token).Wait(token); }
                    catch { goto After; }

                    // новий івент переходу між машинами
                    _hub.Clients.All.SendAsync("OnMachineTransfer", new
                    {
                        lineIdx,
                        chipId = mc.ChipId,
                        fromMachine = m,
                        toMachine = m + 1,
                        transferTime = tp.Transfer
                    });
                }

                // після першої машини — можемо забирати наступну з черги
                if (m == 0)
                {
                    _firstMachineBusy[lineIdx] = false;
                    if (_pendingQueue.TryDequeue(out var pend))
                        DispatchToLine(pend, lineIdx, enqueued: true);
                }
            }

            Interlocked.Increment(ref _completedCount[lineIdx]);
            Interlocked.Decrement(ref _inServiceCount[lineIdx]);

            // фінальне завершення
            _hub.Clients.All.SendAsync("OnCompletion", new
            {
                lineIdx,
                chipId = mc.ChipId,
                timings = mc.Timings,
                wasInQueue
            });

            After: ;
        }

        // по завершенні loop — рахуємо %утилізації
        for (int m = 0; m < MachinesPerLine; m++)
            _stats[lineIdx][m].Utilization = busy[m].Elapsed.TotalSeconds / ShiftDurationSeconds;
    }

    private double NextUniform(double mean, double dev)
        => mean - dev + _rnd.NextDouble() * 2 * dev;

    public SimulationStatsResponse GetStats()
    {
        if (_stats == null)
            return new SimulationStatsResponse
            {
                TotalArrived = 0,
                TotalProcessed = 0,
                TotalUnprocessed = 0,
                Stats = new List<LineStatistics>()
            };

        int totalProcessed = _completedCount.Sum();
        int totalInQueue   = _queues.Sum(q => q.Count);
        int totalInService = _inServiceCount.Sum();
        int totalUnprocessed = totalInQueue + totalInService;

        var stats = Enumerable.Range(0, LinesCount).Select(i => new LineStatistics
        {
            LineNumber     = i + 1,
            CompletedCount = _completedCount[i],
            InQueueCount   = _queues[i].Count,
            InServiceCount = _inServiceCount[i],
            MachineStats   = _stats[i].ToList()
        }).ToList();

        return new SimulationStatsResponse
        {
            TotalArrived     = _totalArrived,
            TotalProcessed   = totalProcessed,
            TotalUnprocessed = totalUnprocessed,
            Stats            = stats
        };
    }
}