namespace ManufacturingChips.Models;

public class EnqueueResult
{
    public int LineIndex { get; set; }
    public double[] ServiceTimes { get; set; }
    public double[] TransferTimes { get; set; }
}