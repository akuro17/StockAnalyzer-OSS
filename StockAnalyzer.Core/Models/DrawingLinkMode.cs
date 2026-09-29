namespace StockAnalyzer.Core.Models;

/// <summary>
/// Specifies what the Layers Panel "link" action does to the positions of the newly linked child drawings.
/// The mode is read once when the link button is pressed and is not stored in the link group.
/// </summary>
public enum DrawingLinkMode
{
    /// <summary>
    /// Each newly linked child is translated (shape preserved) so that its anchor point equals the parent's
    /// anchor point before it joins the group (default).
    /// </summary>
    SnapToParentAnchorPoint = 0,

    /// <summary>
    /// Children join the group without being moved; their current position relative to the parent is kept
    /// and every later linked move translates all members by the same displacement.
    /// </summary>
    PreserveRelativePosition = 1
}
