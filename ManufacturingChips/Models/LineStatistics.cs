namespace ManufacturingChips.Models;
public class LineStatistics
{
    public int LineNumber { get; set; }
    public int CompletedCount { get; set; }
    public int InQueueCount { get; set; }
    public int InServiceCount { get; set; }
    public List<MachineStatistics> MachineStats { get; set; }
}