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

    // Now: one queue per machine, per line
    private BlockingCollection<Microchip>[][] _serviceQueues;

    private MachineStatistics[][] _stats;
    private int[] _inServiceCount;    // number of chips currently in‐service (i.e., between machine 0 start and last machine completion) per line
    private int[] _completedCount;    // number of chips fully processed per line
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

        // Initialize per‐line, per‐machine structures
        _serviceQueues = new BlockingCollection<Microchip>[LinesCount][];
        _stats = new MachineStatistics[LinesCount][];
        _inServiceCount = new int[LinesCount];
        _completedCount = new int[LinesCount];
        _totalArrived = 0;

        for (int i = 0; i < LinesCount; i++)
        {
            // Create one BlockingCollection per machine on this line
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

        // Start arrival generator
        _arrivalTask = Task.Run(() => ArrivalLoop(token), token);

        // Start a MachineLoop task for each line and each machine
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

        // Auto‐stop after shift duration
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

            // Round‐robin assignment (or other policy)
            int lineIdx = _totalArrived % LinesCount;

            // Dispatch directly into machine 0's queue
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

            // Compute queue time for THIS machine
            mc.DequeueTime = DateTime.UtcNow;
            double qTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

            // If this is machine 0, that marks chip entering the line
            if (machineIdx == 0)
            {
                Interlocked.Increment(ref _inServiceCount[lineIdx]);
            }

            // Update stats before service
            var st = _stats[lineIdx][machineIdx];
            st.AverageQueueTime = (st.AverageQueueTime * st.ProcessedCount + qTime)
                                  / (st.ProcessedCount + 1);
            st.MaxQueueLength = Math.Max(st.MaxQueueLength, queue.Count);

            // Perform service
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

            // Update service stats
            st.AverageServiceTime = (st.AverageServiceTime * st.ProcessedCount + tp.Service)
                                    / (st.ProcessedCount + 1);
            st.ProcessedCount++;

            // Perform transfer to next machine (or finish)
            if (machineIdx < MachinesPerLine - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(tp.Transfer), token);

                // Enqueue into next machine's queue
                mc.EnqueueTime = DateTime.UtcNow;
                _serviceQueues[lineIdx][machineIdx + 1].Add(mc);

                // Notify UI about transfer
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
                // Last machine: chip is completed
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

        // Compute utilization after stop
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
