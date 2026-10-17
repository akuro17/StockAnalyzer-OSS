using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>The seam from a Settings page to the main chart (ChartViewModel is a transient service, so a page cannot receive it from DI).</summary>
public interface IRlReplayTarget
{
    /// <summary>Shows the replay on the main chart.</summary>
    Task ApplyReplayAsync(RlQSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Removes the replay from the main chart and restores the chart's normal data (symbol, time frame and range as before the replay).</summary>
    void Clear();
}
