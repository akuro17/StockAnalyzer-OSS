using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>One 192-bar episode cut from the stored daily bars of any ticker, validated like a training source but without an episode catalog.</summary>
internal sealed class RlExternalEpisode
{
    public RlSourceManifest Manifest { get; }
    public string SourceRevision { get; }
    public ImmutableArray<CandleData> Bars { get; }
    public DateOnly FirstBar { get; }
    public DateOnly LastBar { get; }
    /// <summary>The trading day used as the anchor (the 64th bar), which may differ from the requested date.</summary>
    public DateOnly ResolvedAnchorDate { get; }

    public RlExternalEpisode(RlSourceManifest manifest, string sourceRevision, ImmutableArray<CandleData> bars, DateOnly resolvedAnchorDate)
    {
        Manifest = manifest;
        SourceRevision = sourceRevision;
        Bars = bars;
        FirstBar = DateOnly.FromDateTime(bars[0].Timestamp);
        LastBar = DateOnly.FromDateTime(bars[^1].Timestamp);
        ResolvedAnchorDate = resolvedAnchorDate;
    }

    public BacktestInput CreateInput() => RlSourceSnapshot.CreateEpisodeInput(Manifest.Symbol, Bars);
}

/// <summary>
/// Cuts an external replay episode out of a ticker's stored daily bars. The anchor is the 64th bar of the 192-bar episode (the last bar the agent
/// sees at decision 0), so the newest 128 completed bars and the first 63 bars of a ticker can never be anchors (the engine's RL contract).
/// </summary>
internal static class RlExternalEpisodeFactory
{
    private const string CalendarFolder = "calendar";
    private const string EpisodeFolder = "episode";

    /// <summary>The first and last anchor date a ticker's completed stored bars allow.</summary>
    public static async Task<RlAnchorRange> ReadAnchorRangeAsync(string symbol, TrainingResourceLimits limits, RlReplayWorkspace workspace, CancellationToken ct)
    {
        var dates = (await ReadCalendarAsync(symbol, Path.Combine(CallDirectory(workspace), CalendarFolder), limits, null, null, ct).ConfigureAwait(false)).Dates;
        RequireHistory(dates);
        return new RlAnchorRange(dates[RlContract.WindowLength - 1], dates[LastAnchorIndex(dates)]);
    }

    /// <summary>
    /// Cuts the episode of one anchor date. The ticker's stored files are captured ONCE into the call's episode folder: the calendar, the
    /// resolved anchor, the exchange date and the episode bars all come from that one copy, so a stored file that changes meanwhile cannot make
    /// them disagree. <paramref name="afterCapture"/> (tests only) is called with that folder right after the capture. <paramref name="timings"/>
    /// receives the milliseconds of the capture, the calendar read and the episode write and validation, and the size of what was read.
    /// </summary>
    public static async Task<RlExternalEpisode> PrepareAsync(string symbol, DateOnly anchorDate, TrainingResourceLimits limits,
        RlReplayWorkspace workspace, CancellationToken ct, Action<string>? afterCapture = null, RlReplayTimings? timings = null)
    {
        string directory = Path.Combine(CallDirectory(workspace), EpisodeFolder);
        var calendar = await ReadCalendarAsync(symbol, directory, limits, afterCapture, timings, ct).ConfigureAwait(false);
        var dates = calendar.Dates;
        RequireHistory(dates);
        ct.ThrowIfCancellationRequested();
        int anchor = Resolve(dates, anchorDate);
        if (anchor < RlContract.WindowLength - 1)
            throw new RlExternalReplayException(RlExternalReplayRejection.NotEnoughBarsBefore,
                $"Fewer than {RlContract.WindowLength} stored bars up to the anchor date.", earliestAnchor: dates[RlContract.WindowLength - 1]);
        int first = anchor - (RlContract.WindowLength - 1);
        if (first + RlContract.SupportLength - 1 >= dates.Length)
            throw new RlExternalReplayException(RlExternalReplayRejection.NotEnoughBarsAfter,
                $"Fewer than {RlContract.EpisodeHorizon} completed stored bars after the anchor date.", latestAnchor: dates[LastAnchorIndex(dates)]);

        DateOnly start = dates[first], endExclusive = dates[first + RlContract.SupportLength - 1].AddDays(1);
        long episodeStart = RlReplayTimings.Start();
        try
        {
            await RlLocalSnapshotFactory.WriteSourceFilesFromCaptureAsync(calendar.Captured, calendar.Started, symbol.Trim(), start, endExclusive,
                directory, limits, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var validated = RlSourceSnapshot.ValidateSourceFiles(directory, limits, ct);
            if (validated.Bars.Length != RlContract.SupportLength)
                throw new InvalidDataException("The stored bars changed while the replay episode was prepared.");
            ct.ThrowIfCancellationRequested();
            if (timings is not null) timings.EpisodeMs = RlReplayTimings.Since(episodeStart);
            return new RlExternalEpisode(validated.Manifest, validated.Revision, validated.Bars, dates[anchor]);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            throw Unavailable(ex);
        }
    }

    /// <summary>The captured copy of a ticker's stored files, the instant taken immediately before the capture, and the completed days read from that copy.</summary>
    private sealed record CapturedCalendar(ImmutableArray<DateOnly> Dates, RlLocalSourceFiles.Snapshot Captured, DateTimeOffset Started);

    /// <summary>
    /// Captures the ticker's stored files into <paramref name="directory"/> and reads the completed trading days (strictly before the exchange date
    /// at the instant of the capture) from that copy, without a row ceiling and without reading a price.
    /// </summary>
    private static async Task<CapturedCalendar> ReadCalendarAsync(string symbol, string directory, TrainingResourceLimits limits,
        Action<string>? afterCapture, RlReplayTimings? timings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new RlExternalReplayException(RlExternalReplayRejection.NoLocalData, "No stored daily data exists for this ticker.");
        try
        {
            var started = DateTimeOffset.UtcNow;
            long captureStart = RlReplayTimings.Start();
            var captured = await RlLocalSourceFiles.CaptureAsync(symbol.Trim(), directory, limits, ct).ConfigureAwait(false);
            if (timings is not null) timings.CaptureMs = RlReplayTimings.Since(captureStart);
            afterCapture?.Invoke(directory);
            long calendarStart = RlReplayTimings.Start();
            var metadata = RlLocalSnapshotFactory.ReadEquityMetadata(Path.Combine(directory, RlFiles.MetadataParquet));
            var exchangeDate = RlLocalSnapshotFactory.ExchangeDate(metadata.Zone, started);
            var dates = await Task.Run(() => RlLocalParquetReader.ReadDates(Path.Combine(directory, RlFiles.RawParquet), exchangeDate, limits, ct), ct).ConfigureAwait(false);
            if (timings is not null)
            {
                timings.CalendarMs = RlReplayTimings.Since(calendarStart);
                timings.Rows = dates.Length;
                timings.RawBytes = new FileInfo(Path.Combine(directory, RlFiles.RawParquet)).Length;
            }
            return new CapturedCalendar(dates, captured, started);
        }
        catch (FileNotFoundException ex)
        {
            throw new RlExternalReplayException(RlExternalReplayRejection.NoLocalData, "No stored daily data exists for this ticker.", inner: ex);
        }
        catch (InvalidDataException ex)
        {
            throw Unavailable(ex);
        }
    }

    /// <summary>A fresh directory per call, so one workspace can serve several calls without their copies colliding.</summary>
    private static string CallDirectory(RlReplayWorkspace workspace) => Path.Combine(workspace.Directory, Guid.NewGuid().ToString("N"));

    private static void RequireHistory(ImmutableArray<DateOnly> dates)
    {
        if (dates.Length < RlContract.SupportLength)
            throw new RlExternalReplayException(RlExternalReplayRejection.NotEnoughHistory,
                $"Fewer than {RlContract.SupportLength} completed stored daily bars.");
    }

    /// <summary>Index of the newest anchor: the episode needs <see cref="RlContract.EpisodeHorizon"/> completed bars after it.</summary>
    private static int LastAnchorIndex(ImmutableArray<DateOnly> dates) => dates.Length - 1 - RlContract.EpisodeHorizon;

    /// <summary>
    /// The existing chart anchor rule (exact trading day, else the nearest earlier day, else the nearest later one) applied to the ticker's calendar.
    /// The resolver reads timestamps only, so the calendar is presented to it as date-stamped bars.
    /// </summary>
    private static int Resolve(ImmutableArray<DateOnly> dates, DateOnly anchorDate)
    {
        var calendar = dates.Select(date => new CandleData(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), 1m, 1m, 1m, 1m, 0)).ToArray();
        return ChartAnchorDateResolver.Resolve(calendar, anchorDate.ToDateTime(TimeOnly.MinValue), TimeFrame.D1)
            ?? throw new InvalidOperationException("The calendar of a ticker with enough bars cannot be empty.");
    }

    private static RlExternalReplayException Unavailable(Exception inner)
        => new(RlExternalReplayRejection.SourceUnavailable, "The stored bars of this ticker cannot be used for a replay: " + inner.Message, inner: inner);
}
