namespace ManufacturingChips.Models;

public class SimulationStatsResponse
{
    public int TotalArrived { get; set; }
    public int TotalProcessed { get; set; }
    public int TotalUnprocessed { get; set; }
    public List<LineStatistics> Stats { get; set; }
}