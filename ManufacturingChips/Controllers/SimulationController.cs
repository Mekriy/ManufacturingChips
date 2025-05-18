using Microsoft.AspNetCore.Mvc;
using ManufacturingChips.Models;
using ManufacturingChips.Services;

namespace ManufacturingChips.Controllers;

public class SimulationController : Controller
{
    private static readonly SimulationService _sim = new SimulationService();

    [HttpGet]
    public IActionResult Index(
        int LinesCount = 3,
        int MachinesPerLine = 4,
        int ShiftDurationSeconds = 120)
    {
        var model = new SimulationView
        {
            LinesCount = LinesCount,
            MachinesPerLine = MachinesPerLine,
            ShiftDurationSeconds = ShiftDurationSeconds
        };
        return View(model);
    }

    [HttpPost]
    public IActionResult StartSimulation([FromBody] SimulationView vm)
    {
        _sim.Start(vm.LinesCount, vm.MachinesPerLine, vm.ShiftDurationSeconds);
        return Ok();
    }

    [HttpPost]
    public IActionResult Stop()
    {
        _sim.Stop();
        return Ok();
    }

    [HttpGet]
    public JsonResult IsRunning()
    {
        return Json(_sim.IsRunning);
    }

    [HttpGet]
    public JsonResult GetStats()
    {
        return Json(_sim.GetStats());
    }

    [HttpPost]
    public JsonResult EnqueueNext()
    {
        int lineIdx = _sim.EnqueueNext();
        return Json(new { lineIndex = lineIdx });
    }
}