using ManufacturingChips.Models;

namespace ManufacturingChips.Interfaces;

public interface ISimulationService
{
    public void Start(int linesCount, int machinesPerLine, int shiftDurationSeconds);
    public void Stop();
    public SimulationStatsResponse GetStats();
    bool IsRunning { get; }
}