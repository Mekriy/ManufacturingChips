using System.Collections.Concurrent;

namespace ManufacturingChips.Models;

public class Machine
{
    private readonly ConcurrentQueue<Product> _queue = new ConcurrentQueue<Product>();
    private readonly object _statLock = new object();

    public int MaxQueueLength { get; set; } = 0;
    private TimeSpan BusyTime { get; set; } = TimeSpan.Zero;
    private TimeSpan TotalQueueTime { get; set; } = TimeSpan.Zero;
    public int ProcessedCount { get; set; } = 0;

    public void Enqueue(Product product, int idx)
    {
        product.EnterQueueAt[idx] = DateTime.Now;
        _queue.Enqueue(product);
        lock (_statLock)
        {
            MaxQueueLength = Math.Max(MaxQueueLength, _queue.Count);
        }
    }

    public void ProcessNext(int serviceTimeMinutes, int idx)
    {
        if (!_queue.TryDequeue(out Product product)) return;
        product.LeaveQueueAt[idx] = DateTime.Now;
        var queueTime = product.LeaveQueueAt[idx] - product.EnterQueueAt[idx];

        lock (_statLock)
        {
            TotalQueueTime += queueTime;
        }

        var start = DateTime.Now;
        Thread.Sleep(TimeSpan.FromMinutes(serviceTimeMinutes));
        var processingTime = DateTime.Now - start;

        lock (_statLock)
        {
            BusyTime += processingTime;
            ProcessedCount++;
        }
    }

    public double Utilization(TimeSpan shiftDuration)
    {
        return BusyTime.TotalMinutes / shiftDuration.TotalMinutes;
    }

    public double AverageQueueTime()
    {
        return ProcessedCount > 0 ? TotalQueueTime.TotalMinutes / ProcessedCount : 0;
    }
}