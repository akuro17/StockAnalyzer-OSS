using StockAnalyzer.Avalonia.Views.Chart;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// Follow-up to "SubWindow Drawing Tools — coordinate fixes": an in-progress drawing/drag gesture
/// must stay bound to the panel it started on for its whole lifetime, the main chart (-1) included.
/// Before this, only sub-panel-origin gestures were pinned, so straying the pointer over a sub-panel
/// mid-draw on the main chart swapped the coordinate transform and wrote the point back in the
/// sub-panel's value scale, producing a phantom mark that wandered the lower chart.
/// </summary>
public class ChartBaseControlGesturePanelTests
{
    [Theory]
    // No gesture active: the panel under the pointer governs (unchanged idle-hover routing).
    [InlineData(null, -1, -1)]   // pointer on main chart
    [InlineData(null, 0, 0)]     // pointer on sub-panel 0
    [InlineData(null, 2, 2)]     // pointer on sub-panel 2
    [InlineData(null, -2, -2)]   // pointer in an outside gap
    [InlineData(null, -3, -3)]   // pointer on the reserved volume band
    // Gesture active on the main chart: stays on the main chart wherever the pointer goes.
    [InlineData(-1, 0, -1)]
    [InlineData(-1, 2, -1)]
    [InlineData(-1, -3, -1)]
    // Gesture active in a sub-panel: stays in that sub-panel wherever the pointer goes (T5 intact).
    [InlineData(1, -1, 1)]
    [InlineData(1, 3, 1)]
    [InlineData(0, -3, 0)]
    public void ResolveGesturePanelIndex_PinsActiveGestureToItsOriginPanel(
        int? activeGesturePanelIndex, int pointerPanelIndex, int expected)
        => Assert.Equal(expected, ChartBaseControl.ResolveGesturePanelIndex(activeGesturePanelIndex, pointerPanelIndex));
}
