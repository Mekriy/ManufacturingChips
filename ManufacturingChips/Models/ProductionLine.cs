using System.Collections.Concurrent;

namespace ManufacturingChips.Models;

public class ProductionLine
{
    private readonly Machine[] _machines;
    private readonly Random _rnd = new();
    private readonly int[] _serviceTimes = { 12, 13, 7, 8 };
    private readonly int[] _serviceDeviations = { 1, 3, 1, 3 };
    private readonly int[] _transportTimes = { 2, 1, 3 };
    private readonly int[] _transportDeviations = { 1, 1, 1 };

    public ProductionLine(int machinesCount)
    {
        _machines = new Machine[machinesCount];
        for (var i = 0; i < machinesCount; i++)
            _machines[i] = new Machine();
    }

    public void Start(BlockingCollection<Product> inputQueue, TimeSpan shiftDuration, CancellationToken token)
    {
        var endTime = DateTime.UtcNow + shiftDuration;

        while ((DateTime.UtcNow < endTime || !inputQueue.IsCompleted) 
               && !token.IsCancellationRequested)
        {
            if (inputQueue.TryTake(out var product, 100))
                ProcessProduct(product, token);
        }
    }

    private void ProcessProduct(Product product, CancellationToken token)
    {
        for (var i = 0; i < _machines.Length && !token.IsCancellationRequested; i++)
        {
            _machines[i].Enqueue(product, i);
            _machines[i].ProcessNext(SampleServiceTime(i), i);

            if (i < _machines.Length - 1)
                Thread.Sleep(TimeSpan.FromSeconds(SampleTransportTime(i)));
        }
    }

    private int SampleServiceTime(int idx)
    {
        return _rnd.Next(
            _serviceTimes[idx] - _serviceDeviations[idx],
            _serviceTimes[idx] + _serviceDeviations[idx] + 1);
    }

    private int SampleTransportTime(int idx)
    {
        return _rnd.Next(
            _transportTimes[idx] - _transportDeviations[idx],
            _transportTimes[idx] + _transportDeviations[idx] + 1);
    }

    public LineStatistics CollectStatistics(int lineNumber, TimeSpan shiftDuration)
    {
        return new LineStatistics
        {
            LineNumber = lineNumber,
            MachineStats = _machines
                .Select((m, idx) => new MachineStatistics
                {
                    MachineIndex      = idx + 1,
                    Utilization       = m.Utilization(shiftDuration),
                    AverageQueueTime  = m.AverageQueueTime(),
                    AverageServiceTime= m.AverageServiceTime(),
                    MaxQueueLength    = m.MaxQueueLength,
                    ProcessedProducts = m.ProcessedCount
                })
                .ToList()
        };
    }
}