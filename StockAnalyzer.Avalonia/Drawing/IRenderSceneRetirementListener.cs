using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// A render scene that keeps references to the objects of a <see cref="ChartObjectManager"/> between frames (<see cref="LayeredDrawingScene"/>).
/// The manager tells it which objects it has removed, <b>before</b> it decides whether it may dispose them, so a frame that starts after the notice
/// can no longer reach a removed object through the scene; a frame that started before it is covered by the render scope
/// (<see cref="ChartObjectManager.BeginRenderScope"/>), which defers the disposal until that frame has ended.
/// </summary>
public interface IRenderSceneRetirementListener
{
    /// <summary>
    /// Called on the thread that changes the manager, outside the manager's lock, once per removal operation.
    /// Only objects that no context of the manager holds any more are listed. The listener must drop them from every array it renders from
    /// (swap in new arrays; never edit an array a frame may be walking) and must not throw.
    /// </summary>
    void OnObjectsRetired(IReadOnlyList<IChartObject> retiredObjects);
}
