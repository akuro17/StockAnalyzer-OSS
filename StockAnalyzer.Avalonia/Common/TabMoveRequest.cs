namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Typed request to move a tab from one index to another inside a single tab container.
/// Carried as the command parameter of <c>TabReorderBehavior.MoveCommand</c>.
/// Lives in Common so ViewModels do not depend on the View-layer Behaviors namespace.
/// </summary>
/// <param name="SourceIndex">0-based index of the dragged tab.</param>
/// <param name="TargetIndex">0-based index of the drop target (unclamped, as raised by the gesture).</param>
public readonly record struct TabMoveRequest(int SourceIndex, int TargetIndex);
