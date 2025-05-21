using ManufacturingChips.Interfaces;
using Microsoft.AspNetCore.Mvc;
using ManufacturingChips.Models;

namespace ManufacturingChips.Controllers;

public class SimulationController : Controller
{
    private readonly ISimulationService _sim;

    public SimulationController(ISimulationService sim)
    {
        _sim = sim;
    }

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
}