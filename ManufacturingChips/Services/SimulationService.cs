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

    private BlockingCollection<Microchip>[] _serviceQueues;
    private Queue<Microchip>[] _pendingQueues;
    private object[] _lineLocks;

    private MachineStatistics[][] _stats;
    private int[] _inServiceCount;
    private int[] _completedCount;
    private bool[] _firstMachineBusy;
    private int _totalArrived;
    private readonly Random _rnd = new();

    public int LinesCount { get; private set; }
    public int MachinesPerLine { get; private set; }
    public int ShiftDurationSeconds { get; private set; }
    public bool IsRunning { get; private set; }

    public SimulationService(IHubContext<SimulationHub> hub)
    {
        _hub = hub;
    }

    public void Start(int linesCount, int machinesPerLine, int shiftDurationSeconds)
    {
        if (IsRunning) return;

        LinesCount = linesCount;
        MachinesPerLine = machinesPerLine;
        ShiftDurationSeconds = shiftDurationSeconds;

        // Initialize per-line structures
        _serviceQueues = new BlockingCollection<Microchip>[LinesCount];
        _pendingQueues = new Queue<Microchip>[LinesCount];
        _lineLocks = new object[LinesCount];
        _stats = new MachineStatistics[LinesCount][];
        _inServiceCount = new int[LinesCount];
        _completedCount = new int[LinesCount];
        _firstMachineBusy = new bool[LinesCount];
        _totalArrived = 0;

        for (int i = 0; i < LinesCount; i++)
        {
            _serviceQueues[i] = new BlockingCollection<Microchip>();
            _pendingQueues[i] = new Queue<Microchip>();
            _lineLocks[i] = new object();
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

        // Start arrival generator
        _arrivalTask = Task.Run(() => ArrivalLoop(token), token);

        // Start line processors
        _lineTasks = Enumerable.Range(0, LinesCount)
            .Select(idx => Task.Run(() => LineLoop(idx, token), token))
            .ToList();

        // Auto-stop after shift duration
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
            var delay = NextUniform(2.5, 0.5);
            try { Task.Delay(TimeSpan.FromSeconds(delay), token).Wait(token); }
            catch { break; }

            var mc = new Microchip
            {
                ChipId = Guid.NewGuid(),
                EnqueueTime = DateTime.UtcNow
            };
            Interlocked.Increment(ref _totalArrived);

            // Round-robin assignment (or other policy)
            int lineIdx = _totalArrived % LinesCount;
            EnqueueOrDispatch(mc, lineIdx);
        }
    }

    private void EnqueueOrDispatch(Microchip mc, int lineIdx)
    {
        lock (_lineLocks[lineIdx])
        {
            if (!_firstMachineBusy[lineIdx] && _serviceQueues[lineIdx].Count == 0)
            {
                DispatchToService(mc, lineIdx);
            }
            else
            {
                _pendingQueues[lineIdx].Enqueue(mc);
                _hub.Clients.All.SendAsync("OnArrival", new { lineIdx, chipId = mc.ChipId });
            }
        }
    }

    private void DispatchToService(Microchip mc, int lineIdx)
    {
        mc.Timings = GenerateTimings();
        _serviceQueues[lineIdx].Add(mc);
        _hub.Clients.All.SendAsync("OnQueueToService", new { lineIdx, chipId = mc.ChipId });
    }

    private List<TimePair> GenerateTimings()
    {
        var timings = new List<TimePair>(MachinesPerLine);
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
        return timings;
    }

    private async Task LineLoop(int lineIdx, CancellationToken token)
    {
        var queue = _serviceQueues[lineIdx];
        var busy = Enumerable.Range(0, MachinesPerLine)
            .Select(_ => new Stopwatch()).ToArray();

        while (!token.IsCancellationRequested)
        {
            Microchip mc;
            try { mc = queue.Take(token); }
            catch { break; }

            mc.DequeueTime = DateTime.UtcNow;
            double qTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

            try
            {
                _firstMachineBusy[lineIdx] = true;
                Interlocked.Increment(ref _inServiceCount[lineIdx]);

                for (int m = 0; m < MachinesPerLine; m++)
                {
                    var st = _stats[lineIdx][m];
                    st.AverageQueueTime = (st.AverageQueueTime * st.ProcessedCount + qTime)
                                          / (st.ProcessedCount + 1);
                    st.MaxQueueLength = Math.Max(st.MaxQueueLength, queue.Count);

                    var tp = mc.Timings[m];
                    busy[m].Start();
                    await Task.Delay(TimeSpan.FromSeconds(tp.Service), token);
                    busy[m].Stop();

                    st.AverageServiceTime = (st.AverageServiceTime * st.ProcessedCount + tp.Service)
                                            / (st.ProcessedCount + 1);
                    st.ProcessedCount++;

                    await Task.Delay(TimeSpan.FromSeconds(tp.Transfer), token);
                    await _hub.Clients.All.SendAsync("OnMachineTransfer", new
                    {
                        lineIdx,
                        chipId = mc.ChipId,
                        fromMachine = m,
                        toMachine = m + 1
                    });
                }

                Interlocked.Increment(ref _completedCount[lineIdx]);
            }
            finally
            {
                _firstMachineBusy[lineIdx] = false;
                Interlocked.Decrement(ref _inServiceCount[lineIdx]);

                Microchip next = null;
                lock (_lineLocks[lineIdx])
                {
                    if (_pendingQueues[lineIdx].Count > 0)
                        next = _pendingQueues[lineIdx].Dequeue();
                }
                if (next != null)
                    DispatchToService(next, lineIdx);
            }
        }

        // compute utilization after stop
        for (int m = 0; m < MachinesPerLine; m++)
            _stats[lineIdx][m].Utilization = busy[m].Elapsed.TotalSeconds / ShiftDurationSeconds;
    }

    private double NextUniform(double mean, double dev)
        => mean - dev + _rnd.NextDouble() * 2 * dev;

    public SimulationStatsResponse GetStats()
    {
        var totalProcessed = _completedCount.Sum();
        var totalInQueue = _serviceQueues.Sum(q => q.Count);
        var totalInService = _inServiceCount.Sum();

        var statsList = Enumerable.Range(0, LinesCount).Select(i => new LineStatistics
        {
            LineNumber = i + 1,
            CompletedCount = _completedCount[i],
            InQueueCount = _serviceQueues[i].Count,
            InServiceCount = _inServiceCount[i],
            MachineStats = _stats[i].ToList()
        }).ToList();

        return new SimulationStatsResponse
        {
            TotalArrived = _totalArrived,
            TotalProcessed = totalProcessed,
            TotalUnprocessed = totalInQueue + totalInService,
            Stats = statsList
        };
    }
}