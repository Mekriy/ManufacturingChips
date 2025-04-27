namespace ManufacturingChips.Models;

public class MachineStatistics
{
    public int MachineIndex { get; set; }
    public double Utilization { get; set; }
    public double AverageServiceQueueTime { get; set; }
    public int MaxQueueLength { get; set; }
    public int ProcessedProducts { get; set; }
}