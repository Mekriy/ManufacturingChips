using System.Collections.Concurrent;
using ManufacturingChips.Models;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingChips.Controllers;

public class SimulationController : Controller
{
    private static CancellationTokenSource _cts;
    private static BlockingCollection<Product> _inputQueue;
    private static Task _generatorTask;
    private static Task[] _lineTasks;

    private static int _totalGenerated;
    private static List<LineStatistics> _latestStats;

    private static int _lastLinesCount = 3;
    private static int _lastMachinesPerLine = 4;
    private static int _lastShiftDurationSeconds = 60;

    private static bool _isRunning = false;

    [HttpGet]
    public IActionResult Index()
    {
        var vm = new SimulationView
        {
            LinesCount = _lastLinesCount,
            MachinesPerLine = _lastMachinesPerLine,
            ShiftDurationSeconds = _lastShiftDurationSeconds
        };
        return View(vm);
    }

    [HttpPost]
    [Route("Simulation/StartSimulation")]
    public IActionResult StartSimulation([FromBody] SimulationView vm)
    {
        _lastLinesCount = vm.LinesCount;
        _lastMachinesPerLine = vm.MachinesPerLine;
        _lastShiftDurationSeconds = vm.ShiftDurationSeconds;

        _isRunning = true;
        _totalGenerated = 0;
        _latestStats = null;

        _cts = new CancellationTokenSource();
        _inputQueue = new BlockingCollection<Product>();
        var token = _cts.Token;
        var shiftSpan = TimeSpan.FromSeconds(vm.ShiftDurationSeconds);

        _generatorTask = Task.Run(async () =>
        {
            var rnd = new Random();
            var endTime = DateTime.UtcNow + shiftSpan;

            try
            {
                while (DateTime.UtcNow < endTime && !token.IsCancellationRequested)
                {
                    _inputQueue.Add(new Product(), token);
                    Interlocked.Increment(ref _totalGenerated);
                    await Task.Delay(TimeSpan.FromSeconds(rnd.Next(8, 13)), token);
                }
            }
            catch (OperationCanceledException) {  }
            finally
            {
                _inputQueue.CompleteAdding();
            }
        }, token);

        var lines = Enumerable.Range(0, vm.LinesCount)
            .Select(_ => new ProductionLine(vm.MachinesPerLine))
            .ToArray();

        _lineTasks = lines
            .Select(ln =>
                Task.Run(() => ln.Start(_inputQueue, shiftSpan, token), token)
            )
            .ToArray();

        Task.Run(async () =>
        {
            try
            {
                await _generatorTask;
                await Task.WhenAll(_lineTasks);
                _latestStats = lines
                    .Select((ln, idx) => ln.CollectStatistics(idx + 1, shiftSpan))
                    .ToList();
            }
            catch (OperationCanceledException) { }
            finally
            {
                _isRunning = false;
            }
        }, CancellationToken.None);

        return Ok();
    }

    [HttpPost]
    public IActionResult Stop()
    {
        if (_isRunning)
        {
            _cts.Cancel();
            _inputQueue?.CompleteAdding();
        }
        return Ok();
    }

    [HttpGet]
    public IActionResult IsRunning()
        => Json(_isRunning);

    [HttpGet]
    public IActionResult GetStats()
        => Json(new { total = _totalGenerated, stats = _latestStats });
}