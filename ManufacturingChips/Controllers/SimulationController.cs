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

        _generatorTask = Task.Run(() =>
        {
            var rnd = new Random();
            var shiftDuration = TimeSpan.FromSeconds(vm.ShiftDurationSeconds);
            var endTime = DateTime.Now + shiftDuration;

            while (DateTime.Now < endTime && !token.IsCancellationRequested)
            {
                _inputQueue.Add(new Product());
                Interlocked.Increment(ref _totalGenerated);
                Thread.Sleep(TimeSpan.FromSeconds(rnd.Next(8, 13)));
            }
            _inputQueue.CompleteAdding();
        }, token);

        var lines = Enumerable.Range(0, vm.LinesCount)
            .Select(_ => new ProductionLine(vm.MachinesPerLine))
            .ToArray();

        _lineTasks = lines
            .Select((ln, idx) =>
                Task.Run(() => ln.Start(_inputQueue, TimeSpan.Zero, CancellationToken.None)))
            .ToArray();

        Task.WhenAll(_generatorTask)
            .ContinueWith(_ =>
                Task.WhenAll(_lineTasks)
                    .ContinueWith(__ =>
                    {
                        _latestStats = lines
                            .Select((ln, idx) => ln.CollectStatistics(idx + 1, TimeSpan.FromSeconds(vm.ShiftDurationSeconds)))
                            .ToList();
                        _isRunning = false;
                    })
            );

        return Ok();
    }

    [HttpPost]
    public IActionResult Stop()
    {
        if (_isRunning && _inputQueue != null && !_inputQueue.IsAddingCompleted)
            _inputQueue.CompleteAdding();
        return Ok();
    }

    [HttpGet]
    public IActionResult IsRunning()
        => Json(_isRunning);

    [HttpGet]
    public IActionResult GetStats()
        => Json(new { total = _totalGenerated, stats = _latestStats });
}
