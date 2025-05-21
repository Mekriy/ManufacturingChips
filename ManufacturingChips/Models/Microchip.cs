namespace ManufacturingChips.Models;
public class Microchip
{
    public Guid ChipId { get; set; }
    public DateTime EnqueueTime { get; set; }
    public DateTime DequeueTime { get; set; }
    public List<TimePair> Timings { get; set; }
}
