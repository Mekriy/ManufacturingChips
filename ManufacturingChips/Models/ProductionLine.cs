using System.Collections.Concurrent;
using ManufacturingChips.Models;
using ManufacturingChips.Services;

namespace ManufacturingChips.Services;
public class ProductionLine
{
    private readonly MachineService[] _machines;
    private readonly Random _rnd = new();
    private readonly int[] _serviceTimes;
    private readonly int[] _serviceDeviations;
    private readonly int[] _transportTimes;
    private readonly int[] _transportDeviations;

    public ProductionLine(int machinesCount)
    {
        _machines = Enumerable.Range(0, machinesCount)
                      .Select(_ => new MachineService())
                      .ToArray();
        // example params; можна зробити динамічними
        _serviceTimes = new int[machinesCount];
        _serviceDeviations = new int[machinesCount];
        for (int i = 0; i < machinesCount; i++) { _serviceTimes[i] = 10; _serviceDeviations[i] = 2; }
        _transportTimes = new int[machinesCount-1];
        _transportDeviations = new int[machinesCount-1];
        for (int i = 0; i < machinesCount-1; i++) { _transportTimes[i] = 2; _transportDeviations[i] =1; }
    }

    public void Start(
        BlockingCollection<Microchip> myQueue,
        BlockingCollection<(int, Microchip)> completedQueue,
        int lineIndex,
        TimeSpan shiftDuration,
        CancellationToken token)
    {
        var end = DateTime.UtcNow + shiftDuration;
        while ((DateTime.UtcNow < end || !myQueue.IsCompleted)
               && !token.IsCancellationRequested)
        {
            if (myQueue.TryTake(out var chip, 100, token))
            {
                ProcessProduct(chip, lineIndex, completedQueue, token);
            }
        }
    }

    private void ProcessProduct(
        Microchip chip,
        int lineIndex,
        BlockingCollection<(int, Microchip)> completedQueue,
        CancellationToken token)
    {
        for (int m = 0; m < _machines.Length && !token.IsCancellationRequested; m++)
        {
            _machines[m].Enqueue(chip, m);
            _machines[m].ProcessNext(SampleServiceTime(m), m);

            if (m < _machines.Length - 1)
                Thread.Sleep(TimeSpan.FromSeconds(SampleTransportTime(m)));
        }
        completedQueue.Add((lineIndex, chip)); 
    }

    private int SampleServiceTime(int idx)
        => _rnd.Next(_serviceTimes[idx] - _serviceDeviations[idx],
                    _serviceTimes[idx] + _serviceDeviations[idx] + 1);

    private int SampleTransportTime(int idx)
        => _rnd.Next(_transportTimes[idx] - _transportDeviations[idx],
                    _transportTimes[idx] + _transportDeviations[idx] + 1);

    public LineStatistics CollectStatistics(int lineNumber, TimeSpan shiftDuration)
        => new LineStatistics
        {
            LineNumber = lineNumber,
            MachineStats = _machines.Select((m,i) => new MachineStatistics
            {
                MachineIndex        = i + 1,
                Utilization         = m.Utilization(shiftDuration),
                AverageQueueTime    = m.AverageQueueTime(),
                AverageServiceTime  = m.AverageServiceTime(),
                MaxQueueLength      = m.MaxQueueLength,
                ProcessedProducts   = m.ProcessedCount
            }).ToList()
        };
}