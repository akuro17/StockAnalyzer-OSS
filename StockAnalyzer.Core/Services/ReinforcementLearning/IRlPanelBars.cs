using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Read access to the daily bars of a panel by (symbol index, calendar index). The portfolio accountant and observation builder read bars
/// only through it, so a test can substitute real in-memory bars or a recording accessor that proves no future bar is read.
/// </summary>
public interface IRlPanelBars
{
    int SymbolCount { get; }

    /// <summary>True and the bar when the symbol has a bar at the calendar index; false when it is absent there.</summary>
    bool TryGetBar(int symbolIndex, int calendarIndex, out CandleData bar);
}
