using System.Collections.Concurrent;

namespace ManufacturingChips.Models;

public class ProductionLine
{
    private readonly Machine[] _machines = new Machine[4];
    private readonly Random _rnd = new();
    private readonly int[] _serviceTimes = [12, 13, 7, 8];
    private readonly int[] _serviceDeviations = [1, 3, 1, 3];
    private readonly int[] _transportTimes = [2, 1, 3];
    private readonly int[] _transportDeviations = [1, 1, 1];

    public ProductionLine()
    {
        for (var i = 0; i < 4; i++)
            _machines[i] = new Machine();
    }

    public void Start(BlockingCollection<Product> inputQueue, TimeSpan shiftDuration)
    {
        var endTime = DateTime.Now + shiftDuration;

        while (DateTime.Now < endTime || inputQueue.Count > 0)
        {
            if (inputQueue.TryTake(out Product product, Timeout.Infinite))
            {
                ProcessProduct(product);
            }
        }
    }

    private void ProcessProduct(Product product)
    {
        for (var i = 0; i < 4; i++)
        {
            _machines[i].Enqueue(product, i);
            _machines[i].ProcessNext(SampleServiceTime(i), i);

            if (i < 3)
            {
                Thread.Sleep(TimeSpan.FromMinutes(SampleTransportTime(i)));
            }
        }
    }

    private int SampleServiceTime(int idx)
    {
        return _rnd.Next(_serviceTimes[idx] - _serviceDeviations[idx], _serviceTimes[idx] + _serviceDeviations[idx] + 1);
    }

    private int SampleTransportTime(int idx)
    {
        return _rnd.Next(_transportTimes[idx] - _transportDeviations[idx],
            _transportTimes[idx] + _transportDeviations[idx] + 1);
    }

    public LineStatistics CollectStatistics(int lineNumber, TimeSpan shiftDuration)
    {
        return new LineStatistics
        {
            LineNumber = lineNumber,
            MachineStats = _machines.Select((m, idx) => new MachineStatistics
            {
                MachineIndex = idx + 1,
                Utilization = m.Utilization(shiftDuration),
                AverageServiceQueueTime = m.AverageQueueTime(),
                MaxQueueLength = m.MaxQueueLength,
                ProcessedProducts = m.ProcessedCount
            }).ToList()
        };
    }
}