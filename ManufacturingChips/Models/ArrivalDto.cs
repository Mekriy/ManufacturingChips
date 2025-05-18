namespace ManufacturingChips.Models;

public class ArrivalDto
{
    public int LineIdx { get; set; }
    public Guid ChipId { get; set; }
    public List<TimePair> Timings { get; set; }
    public bool Enqueued { get; set; }
}