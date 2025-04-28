namespace ManufacturingChips.Models;

public class SimulationView
{
    public int LinesCount             { get; set; } = 3;
    public int MachinesPerLine        { get; set; } = 4;
    public int ShiftDurationSeconds   { get; set; } = 60;

    public List<string> LogEntries    { get; set; } = new();
    public int TotalGenerated         { get; set; }
    public List<LineStatistics> Results { get; set; } = new();
}