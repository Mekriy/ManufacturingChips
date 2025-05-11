using System.Collections.Concurrent;
using System.Diagnostics;
using ManufacturingChips.Models;

namespace ManufacturingChips.Services;

public class SimulationService
{
    private CancellationTokenSource _cts;
    private Task _simulationTask;
    private readonly object _lock = new();

    private int _lines, _machinesPerLine, _durationSeconds;
    private List<LineStatistics> _lineStats;
    private Stopwatch _stopwatch;

    public bool IsRunning { get; private set; }

    private readonly double arrivalMean = 10;
    private readonly double arrivalDev = 2;

    private readonly double[] serviceMean = { 12, 13, 7, 8 };
    private readonly double[] serviceDev = { 1, 3, 1, 3 };

    private readonly double[] transferMean = { 2, 1, 3 };
    private readonly double[] transferDev = { 1, 1, 1 };

    private double NextUniform(double mean, double dev, Random rnd)
        => mean - dev + rnd.NextDouble() * (2 * dev);

    public void Start(int lines, int machinesPerLine, int durationSeconds)
    {
        if (IsRunning) return;

        _lines = lines;
        _machinesPerLine = machinesPerLine;
        _durationSeconds = durationSeconds;

        // Ініціалізація статистик
        _lineStats = Enumerable.Range(1, lines)
            .Select(i => new LineStatistics
            {
                LineNumber = i,
                MachineStats = Enumerable.Range(0, machinesPerLine)
                    .Select(m => new MachineStatistics { MachineIndex = m })
                    .ToList()
            }).ToList();

        _cts = new CancellationTokenSource();
        _stopwatch = Stopwatch.StartNew();
        IsRunning = true;

        _simulationTask = Task.Run(() => RunSimulation(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts.Cancel();
        try
        {
            _simulationTask.Wait();
        }
        catch
        {
        }

        IsRunning = false;
    }

    private void RunSimulation(CancellationToken token)
    {
        var rnd = new Random();
        var lineQueues = new ConcurrentDictionary<int, ConcurrentQueue<DateTime>>();

        var tasks = Enumerable.Range(0, _lines).Select(lineIdx => Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _stopwatch.Elapsed.TotalSeconds < _durationSeconds)
            {
                // Інтервал між надходженнями (секунди)
                var interarrival = NextUniform(arrivalMean, arrivalDev, rnd);
                await Task.Delay(TimeSpan.FromSeconds(interarrival), token);

                // Енк'ю
                var enqueueTime = DateTime.UtcNow;
                var q = lineQueues.GetOrAdd(lineIdx, _ => new ConcurrentQueue<DateTime>());
                q.Enqueue(enqueueTime);

                lock (_lock)
                {
                    var len = q.Count;
                    foreach (var ms in _lineStats[lineIdx].MachineStats)
                        ms.MaxQueueLength = Math.Max(ms.MaxQueueLength, len);
                }

                // Обслуговування на кожній машині
                for (int m = 0; m < _machinesPerLine; m++)
                {
                    if (q.TryDequeue(out var t0))
                    {
                        var qt = (DateTime.UtcNow - t0).TotalSeconds;
                        lock (_lock) _lineStats[lineIdx].MachineStats[m].AverageQueueTime += qt;
                    }

                    var meanSrv = serviceMean[Math.Min(m, serviceMean.Length - 1)];
                    var devSrv = serviceDev[Math.Min(m, serviceDev.Length - 1)];
                    var serviceTime = NextUniform(meanSrv, devSrv, rnd);
                    lock (_lock)
                    {
                        var ms = _lineStats[lineIdx].MachineStats[m];
                        ms.AverageServiceTime += serviceTime;
                        ms.ProcessedProducts++;
                        ms.Utilization += serviceTime;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(serviceTime), token);

                    // Передача до наступної машини
                    if (m < _machinesPerLine - 1 && m < transferMean.Length)
                    {
                        var tMean = transferMean[m];
                        var tDev = transferDev[m];
                        var transferTime = NextUniform(tMean, tDev, rnd);
                        await Task.Delay(TimeSpan.FromSeconds(transferTime), token);
                    }
                }
            }
        }, token)).ToArray();

        Task.WaitAll(tasks);

        // Обрахунок середніх значень
        lock (_lock)
        {
            foreach (var line in _lineStats)
            foreach (var ms in line.MachineStats)
                if (ms.ProcessedProducts > 0)
                {
                    var p = ms.ProcessedProducts;
                    ms.AverageQueueTime /= p;
                    ms.AverageServiceTime /= p;
                    ms.Utilization /= _durationSeconds;
                }
        }

        _stopwatch.Stop();
        IsRunning = false;
    }

    public SimulationStatsResponse GetStats()
    {
        var total = _lineStats.Sum(l => l.MachineStats.Sum(m => m.ProcessedProducts));
        return new SimulationStatsResponse { Total = total, Stats = _lineStats };
    }
}