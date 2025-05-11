namespace ManufacturingChips.Models;
public class Microchip
{
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime[] EnterQueueAt { get; set; }
    public DateTime[] LeaveQueueAt { get; set; }
    public Microchip(int maxMachinesPerChip = 5)
    {
        EnterQueueAt = new DateTime[maxMachinesPerChip];
        LeaveQueueAt = new DateTime[maxMachinesPerChip];
    }
}
