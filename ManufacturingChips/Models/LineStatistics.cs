namespace ManufacturingChips.Models;

public class LineStatistics
{
    public int LineNumber { get; set; }
    public List<MachineStatistics> MachineStats { get; set; } = new();
}