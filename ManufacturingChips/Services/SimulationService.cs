using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManufacturingChips.Models;

namespace ManufacturingChips.Services
{
    public class SimulationService
    {
        private CancellationTokenSource _cts;
        private Task _arrivalTask;
        private List<Task> _lineTasks;
        private BlockingCollection<Microchip>[] _queues;
        private MachineStatistics[][] _stats;

        private int _totalArrived;
        private int[] _completedCount;
        private int[] _inServiceCount;

        // Черги подій
        private ConcurrentQueue<int> _arrivalEvents;
        private ConcurrentQueue<int> _completionEvents;

        private readonly Random _rnd = new Random();

        public int LinesCount { get; private set; }
        public int MachinesPerLine { get; private set; }
        public int ShiftDurationSeconds { get; private set; }
        public bool IsRunning { get; private set; }

        public void Start(int linesCount, int machinesPerLine, int shiftDurationSeconds)
        {
            if (IsRunning) return;
            LinesCount = linesCount;
            MachinesPerLine = machinesPerLine;
            ShiftDurationSeconds = shiftDurationSeconds;

            // Ініціалізація
            _queues = Enumerable.Range(0, LinesCount)
                                .Select(_ => new BlockingCollection<Microchip>())
                                .ToArray();
            _stats = new MachineStatistics[LinesCount][];
            _completedCount = new int[LinesCount];
            _inServiceCount = new int[LinesCount];
            _totalArrived = 0;
            _arrivalEvents = new ConcurrentQueue<int>();
            _completionEvents = new ConcurrentQueue<int>();

            for (int i = 0; i < LinesCount; i++)
            {
                _stats[i] = Enumerable.Range(0, MachinesPerLine)
                                      .Select(m => new MachineStatistics
                                      {
                                          MachineIndex      = m,
                                          Utilization       = 0,
                                          AverageQueueTime  = 0,
                                          MaxQueueLength    = 0,
                                          AverageServiceTime= 0,
                                          ProcessedCount    = 0
                                      })
                                      .ToArray();
                _completedCount[i] = 0;
                _inServiceCount[i] = 0;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsRunning = true;

            // Авто‐arrival
            _arrivalTask = Task.Run(() => ArrivalLoop(token), token);

            // Кожна лінія
            _lineTasks = Enumerable.Range(0, LinesCount)
                                   .Select(idx => Task.Run(() => LineLoop(idx, token), token))
                                   .ToList();

            // Авто‐стоп
            Task.Run(async () => {
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
                Task.WaitAll(new[] { _arrivalTask }.Concat(_lineTasks).ToArray());
            }
            catch { }
            IsRunning = false;
        }

        private void ArrivalLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                double delay = NextUniform(2.5, 0.5);
                try { Task.Delay(TimeSpan.FromSeconds(delay), token).Wait(token); }
                catch { break; }

                int idx = EnqueueNext_Internal();
                _arrivalEvents.Enqueue(idx);
            }
        }

        private int EnqueueNext_Internal()
        {
            var mc = new Microchip { EnqueueTime = DateTime.UtcNow };
            int idx = _rnd.Next(LinesCount);
            _queues[idx].Add(mc);
            Interlocked.Increment(ref _totalArrived);
            return idx;
        }

        public int EnqueueNext()
        {
            if (!IsRunning) return -1;
            int idx = EnqueueNext_Internal();
            _arrivalEvents.Enqueue(idx);
            return idx;
        }

        // UI викликає цей метод
        public List<int> GetArrivals()
        {
            var outList = new List<int>();
            while (_arrivalEvents.TryDequeue(out int idx))
                outList.Add(idx);
            return outList;
        }

        // UI викликає цей метод
        public List<int> GetCompletions()
        {
            var outList = new List<int>();
            while (_completionEvents.TryDequeue(out int idx))
                outList.Add(idx);
            return outList;
        }

        private void LineLoop(int lineIdx, CancellationToken token)
        {
            var machines = _stats[lineIdx];
            var queue = _queues[lineIdx];
            var busy = Enumerable.Range(0, MachinesPerLine)
                                 .Select(_ => new Stopwatch())
                                 .ToArray();

            while (!token.IsCancellationRequested)
            {
                Microchip mc;
                try { mc = queue.Take(token); }
                catch { break; }

                Interlocked.Increment(ref _inServiceCount[lineIdx]);
                mc.DequeueTime = DateTime.UtcNow;
                double qTime = (mc.DequeueTime - mc.EnqueueTime).TotalSeconds;

                for (int m = 0; m < MachinesPerLine; m++)
                {
                    var st = machines[m];
                    st.AverageQueueTime = 
                        (st.AverageQueueTime * st.ProcessedCount + qTime) / (st.ProcessedCount+1);
                    st.MaxQueueLength = Math.Max(st.MaxQueueLength, queue.Count);

                    double[] means = { 3, 3.75, 1.75, 2.0 };
                    double[] devs  = { 0.25, 0.75, 0.25, 0.75 };
                    double srv = NextUniform(means[m], devs[m]);
                    busy[m].Start();
                    try { Task.Delay(TimeSpan.FromSeconds(srv), token).Wait(token); }
                    catch 
                    {
                        busy[m].Stop();
                        Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                        goto After;
                    }
                    busy[m].Stop();

                    st.AverageServiceTime = 
                        (st.AverageServiceTime * st.ProcessedCount + srv) / (st.ProcessedCount+1);
                    st.ProcessedCount++;

                    if (m < MachinesPerLine-1)
                    {
                        double[] tMeans = { 0.5, 0.25, 0.75 };
                        double[] tDevs  = { 0.25, 0.25, 0.25 };
                        double tr = NextUniform(tMeans[m], tDevs[m]);
                        try { Task.Delay(TimeSpan.FromSeconds(tr), token).Wait(token); }
                        catch 
                        {
                            Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                            goto After;
                        }
                    }
                }

                // успішне завершення
                Interlocked.Increment(ref _completedCount[lineIdx]);
                Interlocked.Decrement(ref _inServiceCount[lineIdx]);
                _completionEvents.Enqueue(lineIdx);

            After:
                ;
            }

            // utilization
            for (int m = 0; m < MachinesPerLine; m++)
                machines[m].Utilization = busy[m].Elapsed.TotalSeconds / ShiftDurationSeconds;
        }

        private double NextUniform(double mean, double dev)
            => mean - dev + _rnd.NextDouble()*(2*dev);

        public SimulationStatsResponse GetStats()
        {
            // Якщо сервіс ще не стартував або _queues не ініціалізовані — повертаємо порожню статистику
            if (_queues == null || _stats == null)
            {
                return new SimulationStatsResponse
                {
                    TotalArrived     = 0,
                    TotalProcessed   = 0,
                    TotalUnprocessed = 0,
                    Stats = new List<LineStatistics>()
                };
            }

            int totalCompleted   = _completedCount.Sum();
            int totalInQueues    = _queues.Sum(q => q.Count);
            int totalInService   = _inServiceCount.Sum();
            int totalUnprocessed = totalInQueues + totalInService;

            var stats = Enumerable.Range(0, LinesCount)
                .Select(i => new LineStatistics
                {
                    LineNumber     = i + 1,
                    CompletedCount = _completedCount[i],
                    InQueueCount   = _queues[i].Count,
                    InServiceCount = _inServiceCount[i],
                    MachineStats   = _stats[i].ToList()
                })
                .ToList();

            return new SimulationStatsResponse
            {
                TotalArrived     = _totalArrived,
                TotalProcessed   = totalCompleted,
                TotalUnprocessed = totalUnprocessed,
                Stats            = stats
            };
        }

    }
}
