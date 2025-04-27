using System.Collections.Concurrent;
using ManufacturingChips.Models;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingChips.Controllers;

public class SimulationController : Controller
{
    public IActionResult Index()
    {
        return View(new SimulationParameters());
    }

    [HttpPost]
    public IActionResult Run(SimulationParameters parameters)
    {
        var inputQueue = new BlockingCollection<Product>();
        var rnd = new Random();
        var shiftDuration = TimeSpan.FromMinutes(parameters.ShiftDurationMinutes);

        var endTime = DateTime.Now + shiftDuration;
        var generatorTask = Task.Run(() =>
        {
            while (DateTime.Now < endTime)
            {
                inputQueue.Add(new Product());
                var delay = rnd.Next(8, 13);
                Thread.Sleep(TimeSpan.FromMinutes(delay));
            }
            inputQueue.CompleteAdding();
        });

        var lines = new List<ProductionLine> { new(), new(), new() };
        var lineTasks = lines.Select(line => Task.Run(() => line.Start(inputQueue, shiftDuration))).ToArray();

        Task.WaitAll(lineTasks);

        var statistics = lines.Select((line, idx) => line.CollectStatistics(idx + 1, shiftDuration)).ToList();

        return View("Result", statistics);
    }
}