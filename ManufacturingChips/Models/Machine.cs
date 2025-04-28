using System.Collections.Concurrent;

namespace ManufacturingChips.Models;

public class Machine
{
    private readonly ConcurrentQueue<Product> _queue = new();
    private readonly object _statLock = new();

    public int MaxQueueLength { get; private set; } = 0;
    private TimeSpan BusyTime { get; set; } = TimeSpan.Zero;
    private TimeSpan TotalQueueTime { get; set; } = TimeSpan.Zero;
    private TimeSpan TotalServiceTime { get; set; } = TimeSpan.Zero;
    public int ProcessedCount { get; private set; } = 0;

    public void Enqueue(Product product, int idx)
    {
        product.EnterQueueAt[idx] = DateTime.UtcNow;
        _queue.Enqueue(product);
        lock (_statLock)
        {
            MaxQueueLength = Math.Max(MaxQueueLength, _queue.Count);
        }
    }

    public void ProcessNext(int serviceTimeSeconds, int idx)
    {
        if (!_queue.TryDequeue(out var product)) return;

        product.LeaveQueueAt[idx] = DateTime.UtcNow;
        var queueTime = product.LeaveQueueAt[idx] - product.EnterQueueAt[idx];
        lock (_statLock) TotalQueueTime += queueTime;

        var start = DateTime.UtcNow;
        Thread.Sleep(TimeSpan.FromSeconds(serviceTimeSeconds));
        var serviceDur = DateTime.UtcNow - start;
        lock (_statLock)
        {
            BusyTime += serviceDur;
            TotalServiceTime += serviceDur;
            ProcessedCount++;
        }
    }

    public double Utilization(TimeSpan shiftDuration)
        => shiftDuration.TotalSeconds > 0
            ? BusyTime.TotalSeconds / shiftDuration.TotalSeconds
            : 0;

    public double AverageQueueTime()
        => ProcessedCount > 0 ? TotalQueueTime.TotalSeconds / ProcessedCount : 0;

    public double AverageServiceTime()
        => ProcessedCount > 0 ? TotalServiceTime.TotalSeconds / ProcessedCount : 0;
}