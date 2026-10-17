using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Thread-safe accumulator for swallowed evaluation faults across screening sites.
/// Captures per-site occurrence counts and the first exception summary per scan cycle.
/// </summary>
public sealed class ScreenerFaultLog
{
    public const string SiteCriteria = "Criteria";
    public const string SiteIndicatorConfigure = "IndicatorConfigure";
    public const string SiteIndicatorCalculate = "IndicatorCalculate";
    public const string SiteMetadataProperty = "MetadataProperty";

    /// <summary>Maximum characters retained from exception messages to avoid excessive memory usage in fault logs.</summary>
    public const int MaxErrorMessageLength = 200;

    private static readonly string[] KnownSites =
    {
        SiteCriteria,
        SiteIndicatorConfigure,
        SiteIndicatorCalculate,
        SiteMetadataProperty
    };

    private sealed class SiteEntry
    {
        private long _count;
        private string? _firstError;
        private readonly object _lock = new();

        public long Count => Interlocked.Read(ref _count);
        public string? FirstError => _firstError;

        public void Record(Exception ex)
        {
            Interlocked.Increment(ref _count);

            if (_firstError == null)
            {
                lock (_lock)
                {
                    if (_firstError == null)
                    {
                        string msg = ex.Message ?? string.Empty;
                        if (msg.Length > MaxErrorMessageLength)
                        {
                            msg = msg.Substring(0, MaxErrorMessageLength);
                        }
                        _firstError = $"{ex.GetType().Name}: {msg}";
                    }
                }
            }
        }

        public (long Count, string? FirstError) TakeSnapshotAndReset()
        {
            lock (_lock)
            {
                long currentCount = Interlocked.Exchange(ref _count, 0L);
                string? firstError = _firstError;
                _firstError = null;
                return (currentCount, firstError);
            }
        }
    }

    private readonly ConcurrentDictionary<string, SiteEntry> _entries = new(StringComparer.Ordinal);

    public ScreenerFaultLog()
    {
        foreach (var site in KnownSites)
        {
            _entries[site] = new SiteEntry();
        }
    }

    /// <summary>
    /// Records an evaluation fault at the specified site.
    /// Thread-safe; increments the per-site counter and records the first error.
    /// </summary>
    public void Record(string site, Exception ex)
    {
        if (ex == null) return;
        var entry = _entries.GetOrAdd(site ?? string.Empty, _ => new SiteEntry());
        entry.Record(ex);
    }

    /// <summary>
    /// Atomically snapshots all sites with non-zero fault counts and resets counters and error details.
    /// </summary>
    public IReadOnlyList<(string Site, long Count, string FirstError)> TakeSnapshotAndReset()
    {
        var snapshot = new List<(string Site, long Count, string FirstError)>();

        foreach (var site in KnownSites)
        {
            if (_entries.TryGetValue(site, out var entry))
            {
                var (count, firstError) = entry.TakeSnapshotAndReset();
                if (count > 0)
                {
                    snapshot.Add((site, count, firstError ?? string.Empty));
                }
            }
        }

        foreach (var kvp in _entries)
        {
            if (Array.IndexOf(KnownSites, kvp.Key) >= 0) continue;
            var (count, firstError) = kvp.Value.TakeSnapshotAndReset();
            if (count > 0)
            {
                snapshot.Add((kvp.Key, count, firstError ?? string.Empty));
            }
        }

        return snapshot;
    }
}
