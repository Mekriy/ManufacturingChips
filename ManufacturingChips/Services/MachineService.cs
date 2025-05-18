// using System.Collections.Concurrent;
// using ManufacturingChips.Models;
//
// namespace ManufacturingChips.Services;
// public class MachineService
// {
//     private readonly ConcurrentQueue<Microchip> _queue = new();
//     private readonly object _statLock = new();
//
//     public int MaxQueueLength { get; private set; } = 0;
//     private TimeSpan BusyTime = TimeSpan.Zero;
//     private TimeSpan TotalQueueTime = TimeSpan.Zero;
//     private TimeSpan TotalServiceTime = TimeSpan.Zero;
//     public int ProcessedCount { get; private set; } = 0;
//
//     public void Enqueue(Microchip chip, int idx)
//     {
//         chip.EnterQueueAt[idx] = DateTime.UtcNow;
//         _queue.Enqueue(chip);
//         lock (_statLock)
//             MaxQueueLength = Math.Max(MaxQueueLength, _queue.Count);
//     }
//
//     public void ProcessNext(int serviceSec, int idx)
//     {
//         if (!_queue.TryDequeue(out var chip)) return;
//         chip.LeaveQueueAt[idx] = DateTime.UtcNow;
//         var qtime = chip.LeaveQueueAt[idx] - chip.EnterQueueAt[idx];
//         lock (_statLock)
//             TotalQueueTime += qtime;
//
//         var start = DateTime.UtcNow;
//         Thread.Sleep(TimeSpan.FromSeconds(serviceSec));
//         var serv = DateTime.UtcNow - start;
//         lock (_statLock)
//         {
//             BusyTime += serv;
//             TotalServiceTime += serv;
//             ProcessedCount++;
//         }
//     }
//
//     public double Utilization(TimeSpan shift) => shift.TotalSeconds > 0
//         ? BusyTime.TotalSeconds / shift.TotalSeconds : 0;
//
//     public double AverageQueueTime() => ProcessedCount>0
//         ? TotalQueueTime.TotalSeconds / ProcessedCount : 0;
//
//     public double AverageServiceTime() => ProcessedCount>0
//         ? TotalServiceTime.TotalSeconds / ProcessedCount : 0;
// }