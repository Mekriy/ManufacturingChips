using System.Collections.Concurrent;
using ManufacturingChips.Models;

namespace ManufacturingChips.Services;

public class SimulationService
{
    private CancellationTokenSource _cts;
    private Task _simulationTask;
    private readonly object _lock = new();

    private int _lines, _machinesPerLine;
    private List<LineStatistics> _lineStats;

    public bool IsRunning { get; private set; }

    // Параметри (в секундах)
    private readonly double arrivalMean = 10, arrivalDev = 2;
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

        // Ініціалізація статистик
        _lineStats = Enumerable.Range(1, _lines)
            .Select(i => new LineStatistics
            {
                LineNumber = i,
                MachineStats = Enumerable.Range(0, _machinesPerLine)
                    .Select(m => new MachineStatistics { MachineIndex = m })
                    .ToList()
            }).ToList();

        _cts = new CancellationTokenSource();
        _cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds)); // Автоматична зупинка

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
            while (!token.IsCancellationRequested)
            {
                // Інтервал надходження
                await Task.Delay(TimeSpan.FromSeconds(NextUniform(arrivalMean, arrivalDev, rnd)), token);

                // Черга
                var enq = DateTime.UtcNow;
                var q = lineQueues.GetOrAdd(lineIdx, _ => new ConcurrentQueue<DateTime>());
                q.Enqueue(enq);
                lock (_lock)
                    foreach (var ms in _lineStats[lineIdx].MachineStats)
                        ms.MaxQueueLength = Math.Max(ms.MaxQueueLength, q.Count);

                // Обробка
                for (int m = 0; m < _machinesPerLine; m++)
                {
                    if (q.TryDequeue(out var t0))
                    {
                        var qt = (DateTime.UtcNow - t0).TotalSeconds;
                        lock (_lock) _lineStats[lineIdx].MachineStats[m].AverageQueueTime += qt;
                    }

                    var srv = NextUniform(serviceMean[Math.Min(m, serviceMean.Length - 1)],
                        serviceDev[Math.Min(m, serviceDev.Length - 1)], rnd);
                    lock (_lock)
                    {
                        var ms = _lineStats[lineIdx].MachineStats[m];
                        ms.AverageServiceTime += srv;
                        ms.ProcessedProducts++;
                        ms.Utilization += srv;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(srv), token);

                    // Передача
                    if (m < _machinesPerLine - 1 && m < transferMean.Length)
                    {
                        var tr = NextUniform(transferMean[m], transferDev[m], rnd);
                        await Task.Delay(TimeSpan.FromSeconds(tr), token);
                    }
                }
            }
        }, token)).ToArray();

        Task.WaitAll(tasks);

        // Фіналізація статистики
        lock (_lock)
        {
            foreach (var line in _lineStats)
            foreach (var ms in line.MachineStats)
                if (ms.ProcessedProducts > 0)
                {
                    var p = ms.ProcessedProducts;
                    ms.AverageQueueTime /= p;
                    ms.AverageServiceTime /= p;
                    ms.Utilization /= _cts.Token.CanBeCanceled ? p : 1; // нормалізація
                }
        }

        IsRunning = false;
    }

    public SimulationStatsResponse GetStats()
    {
        var total = _lineStats.Sum(l => l.MachineStats.Sum(m => m.ProcessedProducts));
        return new SimulationStatsResponse { Total = total, Stats = _lineStats };
    }
}