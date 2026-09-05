namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Holds the most recent startup orphaned-attachment scan result (spec section 4.5) so it can be
/// read later - by the trash dialog's "Orphaned Files" tab (Step 90-1-20) - without re-scanning.
/// Registered as a DI singleton: the app-startup hook (App.axaml.cs) writes to it once per launch,
/// and any view model resolved afterward reads the same instance.
/// </summary>
public sealed class OrphanedAttachmentScanResultHolder
{
    private readonly object _lock = new();
    private OrphanedAttachmentReport? _latestReport;

    /// <summary>Null until the startup scan completes; never mutated concurrently thanks to the lock.</summary>
    public OrphanedAttachmentReport? LatestReport
    {
        get { lock (_lock) { return _latestReport; } }
    }

    public void SetLatestReport(OrphanedAttachmentReport report)
    {
        lock (_lock) { _latestReport = report; }
    }
}
