using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// An <see cref="IDataService"/> decorator that serves the bars of a <see cref="ScreenerBarSelection"/>: every request reads the whole stored daily history of the
/// symbol from the inner service, keeps the days dated on or before the as-of date (if any), builds the requested time frame from them
/// (<see cref="AsOfCandleWindow"/>: the daily series is cut first, then aggregated, so no bar holds a later day) and, for a weekly or monthly frame with
/// <see cref="ScreenerBarSelection.CompleteBarsOnly"/>, leaves out a last period that has not ended. A Screener scan over it cannot read a bar after the date.
/// Used for one scan; what it served is remembered (symbols without a bar, partial last periods) and read by the gateway afterwards.
/// </summary>
internal sealed class ScreenerBarsDataService : IDataService
{
    private readonly IDataService _inner;
    private readonly ConcurrentDictionary<string, byte> _withoutBars = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateOnly> _evaluatedPartial = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateOnly> _skippedPartial = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<TimeFrame, FrameRecord> _frameRecords = new();
    private readonly ConcurrentDictionary<string, byte> _withoutDailyBars = new(StringComparer.OrdinalIgnoreCase);

    public ScreenerBarsDataService(IDataService inner, ScreenerBarSelection selection)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    public ScreenerBarSelection Selection { get; }

    /// <summary>The symbols asked for that had no bar to serve (no stored file, only later bars, or only a skipped partial period), ordinal order.</summary>
    public IReadOnlyList<string> SymbolsWithoutBars => _withoutBars.Keys.OrderBy(symbol => symbol, StringComparer.Ordinal).ToList();

    /// <summary>For every symbol served with a partial last period: the first day of that period.</summary>
    public IReadOnlyDictionary<string, DateOnly> EvaluatedPartial => _evaluatedPartial;

    /// <summary>For every symbol whose partial last period was left out: the first day of that period.</summary>
    public IReadOnlyDictionary<string, DateOnly> SkippedPartial => _skippedPartial;

    /// <summary>
    /// What a scan over this service served, restricted to <paramref name="scannedSymbols"/> (the symbols the scan evaluated): a comparison symbol is read through the same
    /// service, and its missing bars or partial period are not the scan's. The one place this answer is put together (MCP gateway and the Screener tab).
    /// </summary>
    public ScreenerBarsResult ToResult(IReadOnlyList<string> matched, IEnumerable<string> scannedSymbols)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentNullException.ThrowIfNull(scannedSymbols);
        var scanned = new HashSet<string>(scannedSymbols, StringComparer.OrdinalIgnoreCase);
        return new ScreenerBarsResult(
            matched,
            SymbolsWithoutBars.Where(scanned.Contains).ToList(),
            EvaluatedPartial.Where(pair => scanned.Contains(pair.Key)).Select(pair => pair.Value).Distinct().OrderBy(start => start).ToList(),
            SkippedPartial.Count(pair => scanned.Contains(pair.Key)));
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count)
    {
        // Count 0 asks for the whole stored history, so the oldest days are not truncated before the date is applied.
        IReadOnlyList<CandleData> daily = await _inner.LoadCandlesAsync(symbol, TimeFrame.D1, 0).ConfigureAwait(false);
        IReadOnlyList<CandleData> window = AsOfCandleWindow.Build(daily, Selection.AsOfDate ?? DateOnly.MaxValue, timeFrame, 0);

        window = ApplyPartialPeriodRule(symbol, timeFrame, daily, window, _skippedPartial, _evaluatedPartial);

        if (window.Count == 0)
        {
            _withoutBars.TryAdd(symbol, 0);
            return window;
        }

        return count > 0 && window.Count > count ? window.Skip(window.Count - count).ToList() : window;
    }

    /// <summary>
    /// The bars of <paramref name="symbol"/> in every frame of <paramref name="frames"/> (each of <see cref="DataServiceTimeFrames.Supported"/>), for a scan in which each condition
    /// side reads its own frame: the whole stored daily history is read once, cut at the as-of date and aggregated per frame, the partial-period rule is applied per frame, and
    /// the latest <paramref name="count"/> bars of every frame are kept (0 = all). The D1 list is cut exactly like the plain daily scan's, so a D1 side has the same value on both paths.
    /// What the call found (no bar in a frame, partial last periods) is remembered per frame for <see cref="ToSideFramesResult"/>.
    /// </summary>
    public async Task<IScreenerBarSet> LoadBarSetAsync(string symbol, IReadOnlyList<TimeFrame> frames, int count)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        IReadOnlyList<CandleData> daily = await _inner.LoadCandlesAsync(symbol, TimeFrame.D1, 0).ConfigureAwait(false);
        DateOnly endDate = Selection.AsOfDate ?? DateOnly.MaxValue;
        var byFrame = new Dictionary<TimeFrame, IReadOnlyList<CandleData>>(frames.Count);
        foreach (TimeFrame frame in frames)
        {
            if (byFrame.ContainsKey(frame))
            {
                continue;
            }

            if (!DataServiceTimeFrames.Supported.Contains(frame))
            {
                throw new ArgumentOutOfRangeException(nameof(frames), frame, "Only the time frames of DataServiceTimeFrames.Supported can be built from the daily history.");
            }

            FrameRecord record = _frameRecords.GetOrAdd(frame, static _ => new FrameRecord());
            IReadOnlyList<CandleData> window = AsOfCandleWindow.Build(daily, endDate, frame, 0);
            window = ApplyPartialPeriodRule(symbol, frame, daily, window, record.SkippedPartial, record.EvaluatedPartial);
            if (window.Count == 0)
            {
                (frame == TimeFrame.D1 ? _withoutDailyBars : record.WithoutBars).TryAdd(symbol, 0);
            }
            else if (count > 0 && window.Count > count)
            {
                window = window.Skip(window.Count - count).ToList();
            }

            byFrame[frame] = window;
        }

        return new ScreenerBarSet(byFrame);
    }

    /// <summary>
    /// What the scans over this service served, restricted to <paramref name="scannedSymbols"/>: one <see cref="ScreenerFrameReport"/> for every frame of <paramref name="frames"/>
    /// other than D1 (in the order given), and the scanned symbols with no daily bar.
    /// </summary>
    public ScreenerSideFramesResult ToSideFramesResult(IReadOnlyList<string> matched, IEnumerable<string> scannedSymbols, IReadOnlyList<TimeFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentNullException.ThrowIfNull(scannedSymbols);
        ArgumentNullException.ThrowIfNull(frames);
        var scanned = new HashSet<string>(scannedSymbols, StringComparer.OrdinalIgnoreCase);

        var reports = new List<ScreenerFrameReport>();
        foreach (TimeFrame frame in frames.Where(frame => frame != TimeFrame.D1))
        {
            _frameRecords.TryGetValue(frame, out FrameRecord? record);
            reports.Add(new ScreenerFrameReport(
                frame,
                Ordinal(record?.WithoutBars.Keys, scanned),
                (record?.EvaluatedPartial ?? new ConcurrentDictionary<string, DateOnly>()).Where(pair => scanned.Contains(pair.Key)).Select(pair => pair.Value).Distinct().OrderBy(start => start).ToList(),
                record?.SkippedPartial.Count(pair => scanned.Contains(pair.Key)) ?? 0));
        }

        return new ScreenerSideFramesResult(matched, Ordinal(_withoutDailyBars.Keys, scanned), reports);
    }

    private static List<string> Ordinal(IEnumerable<string>? symbols, HashSet<string> scanned)
        => (symbols ?? Array.Empty<string>()).Where(scanned.Contains).OrderBy(symbol => symbol, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The partial-period rule, defined once: a weekly or monthly last bar whose period has not ended (against the reference date) is dropped when
    /// <see cref="ScreenerBarSelection.CompleteBarsOnly"/> (recorded in <paramref name="skipped"/>) or kept and recorded in <paramref name="evaluated"/>. Any other frame is returned as given.
    /// </summary>
    private IReadOnlyList<CandleData> ApplyPartialPeriodRule(
        string symbol,
        TimeFrame timeFrame,
        IReadOnlyList<CandleData> daily,
        IReadOnlyList<CandleData> window,
        ConcurrentDictionary<string, DateOnly> skipped,
        ConcurrentDictionary<string, DateOnly> evaluated)
    {
        if (window.Count > 0 && timeFrame is TimeFrame.W1 or TimeFrame.MN1)
        {
            DateTime reference = ReferenceDate(daily, Selection.AsOfDate);
            CandleData last = window[^1];
            if (TimeFrameAggregator.IsPartialPeriod(last.Timestamp, timeFrame, reference))
            {
                DateOnly periodStart = DateOnly.FromDateTime(TimeFrameAggregator.PeriodStart(last.Timestamp, timeFrame));
                if (Selection.CompleteBarsOnly)
                {
                    skipped.TryAdd(symbol, periodStart);
                    return window.Take(window.Count - 1).ToList();
                }

                evaluated.TryAdd(symbol, periodStart);
            }
        }

        return window;
    }

    /// <summary>What the bar-set loads found in one frame.</summary>
    private sealed class FrameRecord
    {
        public ConcurrentDictionary<string, byte> WithoutBars { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, DateOnly> EvaluatedPartial { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, DateOnly> SkippedPartial { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The date the bars are known up to: the as-of date, but never later than the last stored daily bar (the rule of <c>sa_get_candles</c>).</summary>
    private static DateTime ReferenceDate(IReadOnlyList<CandleData> daily, DateOnly? asOfDate)
    {
        DateTime lastStored = daily[^1].Timestamp.Date;
        return asOfDate is { } asOf && asOf.ToDateTime(TimeOnly.MinValue) < lastStored ? asOf.ToDateTime(TimeOnly.MinValue) : lastStored;
    }
}
