namespace ManufacturingChips.Models;

public class SimulationStatsResponse
{
    public int Total { get; set; }
    public List<LineStatistics> Stats { get; set; }
}