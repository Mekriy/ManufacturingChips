namespace ManufacturingChips.Models;
public class SimulationView
{
    public int LinesCount { get; set; } = 1;
    public int MachinesPerLine { get; set; } = 1;
    public int ShiftDurationSeconds { get; set; } = 10;
}