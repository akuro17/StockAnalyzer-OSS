namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// The pending orders a strategy asks to cancel at a bar's Close (owner decision G4,
/// A1 (Order cancellation)). The engine can hold at most two pending orders (one per slot), so an intent names at
/// most two order ids. An id is not a new Order: it only refers to an already submitted one. Unknown, duplicate and already-terminal ids are ignored.
/// </summary>
public readonly record struct OrderCancellationIntent(long? FirstOrderId, long? SecondOrderId)
{
    /// <summary>Nothing to cancel.</summary>
    public static OrderCancellationIntent None => default;

    /// <summary>True when <paramref name="orderId"/> is one of the (at most two) named ids.</summary>
    public bool Names(long orderId) => FirstOrderId == orderId || SecondOrderId == orderId;
}
