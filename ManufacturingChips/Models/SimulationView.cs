namespace ManufacturingChips.Models;

public class SimulationView
{
    public int LinesCount { get; set; } = 3;
    public int MachinesPerLine { get; set; } = 4;
    public int ShiftDurationSeconds { get; set; } = 60;

    // Лог ходів
    public List<string> LogEntries { get; set; } = [];

    // Результати
    public int TotalGenerated { get; set; }
    public List<LineStatistics> Results { get; set; } = [];
}