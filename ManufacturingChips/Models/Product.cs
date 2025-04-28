namespace ManufacturingChips.Models;

public class Product
{
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime[] EnterQueueAt{ get; set; } = new DateTime[4];
    public DateTime[] LeaveQueueAt{ get; set; } = new DateTime[4];
}