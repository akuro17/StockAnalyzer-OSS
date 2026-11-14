namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Serializes <c>Render</c> against <c>Dispose</c> for a drawing object that owns native Skia resources.
/// A removed object is disposed at once (<c>ChartObjectManager.DisposeIfNoLongerManaged</c>) while a frame that still holds it may be
/// rendering it on another thread; touching a freed <c>SKPaint</c>/<c>SKPath</c> is an <c>AccessViolationException</c> that ends the process.
/// Usage: <c>lock (_gate.SyncRoot) { if (_gate.IsDisposed) return; ...draw... }</c> and, in <c>Dispose</c>,
/// <c>lock (_gate.SyncRoot) { if (!_gate.TryMarkDisposed()) return; ...free... }</c> (the rule of SA_ARCHITECTURE_RULES for native handles
/// used on another thread: shared lock, volatile flag set inside the lock, every entry re-checks under the lock).
/// </summary>
internal sealed class NativeResourceGate
{
    private volatile bool _isDisposed;

    /// <summary>The lock shared by the render path and <c>Dispose</c>.</summary>
    public object SyncRoot { get; } = new object();

    public bool IsDisposed => _isDisposed;

    /// <summary>Call inside <c>lock (SyncRoot)</c>. Returns false when the object was already disposed.</summary>
    public bool TryMarkDisposed()
    {
        if (_isDisposed) return false;
        _isDisposed = true;
        return true;
    }
}
