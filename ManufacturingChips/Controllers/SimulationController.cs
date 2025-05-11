using ManufacturingChips.Models;
using ManufacturingChips.Services;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingChips.Controllers;

public class SimulationController : Controller
{
    private static readonly SimulationService _simService = new();

    [HttpGet]
    public IActionResult Index(int LinesCount = 1, int MachinesPerLine = 1, int ShiftDurationSeconds = 10)
    {
        var vm = new SimulationView
        {
            LinesCount = LinesCount,
            MachinesPerLine = MachinesPerLine,
            ShiftDurationSeconds = ShiftDurationSeconds
        };
        return View(vm);
    }

    [HttpPost]
    public IActionResult StartSimulation([FromBody] SimulationView vm)
    {
        _simService.Start(vm.LinesCount, vm.MachinesPerLine, vm.ShiftDurationSeconds);
        return Ok();
    }

    [HttpPost]
    public IActionResult Stop()
    {
        _simService.Stop();
        return Ok();
    }

    [HttpGet]
    public JsonResult IsRunning() => Json(_simService.IsRunning);

    [HttpGet]
    public JsonResult GetStats() => Json(_simService.GetStats());
}