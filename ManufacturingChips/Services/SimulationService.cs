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
    private List<Task> _machineTasks;

    private BlockingCollection<Microchip>[][] _serviceQueues;

    private MachineStatistics[][] _stats;
    private int[] _inServiceCount;
    private int[] _completedCount;
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

        _serviceQueues = new BlockingCollection<Microchip>[LinesCount][];
        _stats = new MachineStatistics[LinesCount][];
        _inServiceCount = new int[LinesCount];
        _completedCount = new int[LinesCount];
        _totalArrived = 0;

        for (int i = 0; i < LinesCount; i++)
        {
            _serviceQueues[i] = new BlockingCollection<Microchip>[MachinesPerLine];
            _stats[i] = new MachineStatistics[MachinesPerLine];

            for (int m = 0; m < MachinesPerLine; m++)
            {
                _serviceQueues[i][m] = new BlockingCollection<Microchip>();
                _stats[i][m] = new MachineStatistics
                {
                    MachineIndex = m,
                    Utilization = 0,
                    AverageQueueTime = 0,
                    MaxQueueLength = 0,
                    AverageServiceTime = 0,
                    ProcessedCount = 0
                };
            }
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;

        _arrivalTask = Task.Run(() => ArrivalLoop(token), token);

        _machineTasks = new List<Task>();
        for (int lineIdx = 0; lineIdx < LinesCount; lineIdx++)
        {
            for (int machineIdx = 0; machineIdx < MachinesPerLine; machineIdx++)
            {
                int capturedLine = lineIdx;
                int capturedMachine = machineIdx;
                var task = Task.Run(() => MachineLoop(capturedLine, capturedMachine, token), token);
                _machineTasks.Add(task);
            }
        }

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
            Task.WaitAll(new[] { _arrivalTask }.Concat(_machineTasks).ToArray());
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

            int lineIdx = _totalArrived % LinesCount;

            mc.Timings = GenerateTimings();
            _serviceQueues[lineIdx][0].Add(mc);
            _hub.Clients.All.SendAsync("OnQueueToService", new { lineIdx, chipId = mc.ChipId });
        }
    }

    private async Task MachineLoop(int lineIdx, int machineIdx, CancellationToken token)
    {
        var queue = _serviceQueues[lineIdx][machineIdx];
        var busy = new Stopwatch();

        while (!token.IsCancellationRequested)
        {
            Microchip mc;
            try { mc = queue.Take(token); }
            catch { break; }

            mc.DequeueTime = DateTime.UtcNow;
            double qTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

            if (machineIdx == 0)
            {
                Interlocked.Increment(ref _inServiceCount[lineIdx]);
            }

            var st = _stats[lineIdx][machineIdx];
            st.AverageQueueTime = (st.AverageQueueTime * st.ProcessedCount + qTime)
                                  / (st.ProcessedCount + 1);
            st.MaxQueueLength = Math.Max(st.MaxQueueLength, queue.Count);

            var tp = mc.Timings[machineIdx];
            busy.Start();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(tp.Service), token);
            }
            finally
            {
                busy.Stop();
            }

            st.AverageServiceTime = (st.AverageServiceTime * st.ProcessedCount + tp.Service)
                                    / (st.ProcessedCount + 1);
            st.ProcessedCount++;

            if (machineIdx < MachinesPerLine - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(tp.Transfer), token);

                mc.EnqueueTime = DateTime.UtcNow;
                _serviceQueues[lineIdx][machineIdx + 1].Add(mc);

                await _hub.Clients.All.SendAsync("OnMachineTransfer", new
                {
                    lineIdx,
                    chipId = mc.ChipId,
                    fromMachine = machineIdx,
                    toMachine = machineIdx + 1
                });
            }
            else
            {
                Interlocked.Increment(ref _completedCount[lineIdx]);
                Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                await _hub.Clients.All.SendAsync("OnCompletion", new
                {
                    lineIdx,
                    chipId = mc.ChipId,
                    fromMachine = machineIdx,
                    toMachine = machineIdx
                });
            }
        }

        _stats[lineIdx][machineIdx].Utilization = busy.Elapsed.TotalSeconds / ShiftDurationSeconds;
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

    private double NextUniform(double mean, double dev)
        => mean - dev + _rnd.NextDouble() * 2 * dev;

    public SimulationStatsResponse GetStats()
    {
        var totalProcessed = _completedCount.Sum();
        var totalInQueue = _serviceQueues
            .SelectMany(line => line)
            .Sum(q => q.Count);
        var totalInService = _inServiceCount.Sum();

        var statsList = Enumerable.Range(0, LinesCount).Select(i => new LineStatistics
        {
            LineNumber = i + 1,
            CompletedCount = _completedCount[i],
            InQueueCount = _serviceQueues[i].Sum(q => q.Count),
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
