using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>What the client asked of sa_run_backtest beyond the report: all three parts are optional and off by default.</summary>
internal readonly record struct BacktestDiagnosticsRequest(EquityCurveResolution? Curve, int EpisodeCount, bool PeriodicReturns)
{
    public static BacktestDiagnosticsRequest None => new(null, 0, false);

    public bool Any => Curve is not null || EpisodeCount > 0 || PeriodicReturns;
}

/// <summary>
/// The optional diagnostics of sa_run_backtest (equity curve, drawdown episodes, monthly and yearly returns). The numbers come from the Core
/// pure functions over the evaluated equity points (warm-up removed); this class only selects the points, enforces the limits and writes JSON.
/// A part whose arithmetic fails is left out and named in <see cref="UnavailableKey"/> with a reason, so the finished backtest and the other parts survive.
/// </summary>
internal sealed class BacktestDiagnostics
{
    public const string EquityCurveArgument = "includeEquityCurve";
    public const string DrawdownEpisodesArgument = "includeDrawdownEpisodes";
    public const string PeriodicReturnsArgument = "includePeriodicReturns";
    public const string NoCurve = "None";

    /// <summary>Result key listing the requested parts that could not be calculated: [{part (the argument name), reason}].</summary>
    public const string UnavailableKey = "diagnosticsUnavailable";

    private readonly BacktestDiagnosticsRequest _request;
    private readonly ImmutableArray<EquityCurvePoint> _curve;
    private readonly ImmutableArray<RankedDrawdownEpisode> _episodes;
    private readonly PeriodicReturns _periodic;
    private readonly ImmutableArray<(string Part, string Reason)> _unavailable;

    private BacktestDiagnostics(
        BacktestDiagnosticsRequest request,
        ImmutableArray<EquityCurvePoint> curve,
        ImmutableArray<RankedDrawdownEpisode> episodes,
        PeriodicReturns periodic,
        ImmutableArray<(string Part, string Reason)> unavailable)
    {
        _request = request;
        _curve = curve;
        _episodes = episodes;
        _periodic = periodic;
        _unavailable = unavailable;
    }

    /// <summary>The accepted values of includeEquityCurve: "None" and the resolutions of the Core sampler.</summary>
    public static IReadOnlyList<string> CurveNames { get; } = new[] { NoCurve }.Concat(Enum.GetNames<EquityCurveResolution>()).ToArray();

    /// <summary>Maps a validated includeEquityCurve value to a resolution (null for "None"); the value must be one of <see cref="CurveNames"/>.</summary>
    public static EquityCurveResolution? ParseCurve(string name)
    {
        string? match = CurveNames.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{EquityCurveArgument} must be one of {string.Join(", ", CurveNames)}.", EquityCurveArgument);
        }

        return match == NoCurve ? null : Enum.Parse<EquityCurveResolution>(match);
    }

    /// <summary>
    /// A weekly bar is dated by the first trading day of its week (SA_AI_INTEGRATION 0008) but closes on the last one, so its equity cannot be cut at a
    /// month or year end: month and year returns and a month-resolution curve would carry the next month's moves. They are refused for W1 instead of
    /// returning an ambiguous number (owner decision, 2026-10-30). The Weekly and Bar curves and the drawdown episodes are exact on weekly bars.
    /// </summary>
    public static void EnsureTimeFrameSupported(BacktestDiagnosticsRequest request, TimeFrame timeFrame)
    {
        if (timeFrame != TimeFrame.W1)
        {
            return;
        }

        if (request.PeriodicReturns)
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                "Monthly and yearly returns cannot be calculated from weekly bars: a weekly bar cannot be cut at a month or year end.",
                PeriodicReturnsArgument,
                "Use timeFrame D1 or MN1.");
        }

        if (request.Curve == EquityCurveResolution.Monthly)
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                "A monthly equity curve cannot be calculated from weekly bars: a weekly bar cannot be cut at a month end.",
                EquityCurveArgument,
                "Use Weekly or Bar, or timeFrame D1 or MN1.");
        }
    }

    /// <summary>LIMIT_EXCEEDED when a curve of <paramref name="pointCount"/> points is longer than the limit (the curve is never cut).</summary>
    public static void EnsureCurveFits(EquityCurveResolution resolution, int pointCount, McpLimits limits)
    {
        int limit = McpLimitsContract.EffectiveMaxEquityCurvePoints(limits);
        if (pointCount <= limit)
        {
            return;
        }

        throw new ToolException(
            ToolErrorCodes.LimitExceeded,
            $"The {resolution} equity curve has {pointCount} points; the limit is {limit}.",
            EquityCurveArgument,
            resolution == EquityCurveResolution.Monthly
                ? $"Shorten the period, or raise AiIntegration:{nameof(McpLimits.MaxEquityCurvePoints)} (and {nameof(McpLimits.MaxResponseBytes)})."
                : $"Use {nameof(EquityCurveResolution.Weekly)} or {nameof(EquityCurveResolution.Monthly)}, or shorten the period.");
    }

    /// <summary>
    /// Computes the requested parts. The evaluated points are those from <see cref="BacktestEvaluationMetadata.HistoryStartIndex"/> on; E_0 is the
    /// initial capital dated at the requested start. A curve longer than the limit is LIMIT_EXCEEDED, never cut.
    /// </summary>
    public static BacktestDiagnostics Compute(BacktestEvaluationArtifact artifact, BacktestDiagnosticsRequest request, McpLimits limits)
    {
        ImmutableArray<EquityPoint> all = artifact.Result.EquityPoints;
        int skipped = Math.Min(artifact.Metadata.HistoryStartIndex, all.Length);
        ImmutableArray<EquityPoint> evaluated = skipped == 0 ? all : all.RemoveRange(0, skipped);
        return Compute(
            artifact.Result.Configuration.InitialCapital,
            evaluated,
            artifact.Metadata.RequestedStartUtc,
            artifact.Metadata.RequestedEndUtc,
            request,
            limits);
    }

    /// <summary>The calculation behind <see cref="Compute(BacktestEvaluationArtifact, BacktestDiagnosticsRequest, McpLimits)"/> on already selected evaluated points.</summary>
    internal static BacktestDiagnostics Compute(
        decimal initialCapital,
        ImmutableArray<EquityPoint> evaluated,
        DateTime requestedStartUtc,
        DateTime requestedEndUtc,
        BacktestDiagnosticsRequest request,
        McpLimits limits)
    {
        ImmutableArray<(string Part, string Reason)>.Builder unavailable = ImmutableArray.CreateBuilder<(string Part, string Reason)>();

        ImmutableArray<EquityCurvePoint> curve = ImmutableArray<EquityCurvePoint>.Empty;
        if (request.Curve is { } resolution)
        {
            curve = Isolate(unavailable, EquityCurveArgument, () => EquityCurveSampler.Sample(initialCapital, evaluated, resolution), curve);
            EnsureCurveFits(resolution, curve.Length, limits);
        }

        ImmutableArray<RankedDrawdownEpisode> episodes = request.EpisodeCount > 0
            ? Isolate(
                unavailable,
                DrawdownEpisodesArgument,
                () => DrawdownEpisodeEnumerator.Enumerate(initialCapital, requestedStartUtc, evaluated, request.EpisodeCount),
                ImmutableArray<RankedDrawdownEpisode>.Empty)
            : ImmutableArray<RankedDrawdownEpisode>.Empty;

        PeriodicReturns periodic = new(ImmutableArray<PeriodicReturn>.Empty, ImmutableArray<PeriodicReturn>.Empty);
        if (request.PeriodicReturns)
        {
            periodic = Isolate(unavailable, PeriodicReturnsArgument, () => PeriodicReturnsCalculator.Calculate(initialCapital, evaluated, requestedStartUtc, requestedEndUtc), periodic);
        }

        return new BacktestDiagnostics(request, curve, episodes, periodic, unavailable.ToImmutable());
    }

    /// <summary>Runs one part; an arithmetic failure leaves the part out and records why (the other parts and the finished backtest are kept).</summary>
    private static T Isolate<T>(ImmutableArray<(string Part, string Reason)>.Builder unavailable, string part, Func<T> calculation, T fallback)
    {
        try
        {
            return calculation();
        }
        catch (OverflowException)
        {
            unavailable.Add((part, BacktestDiagnosticReasons.ArithmeticOverflow));
        }
        catch (ArithmeticException)
        {
            unavailable.Add((part, BacktestDiagnosticReasons.NonFiniteResult));
        }

        return fallback;
    }

    private bool IsUnavailable(string part) => _unavailable.Any(entry => entry.Part == part);

    /// <summary>Writes the requested parts as properties of the enclosing result object; nothing is written for a part that was not requested or could not be calculated.</summary>
    public void Write(Utf8JsonWriter writer)
    {
        if (_request.Curve is { } resolution && !IsUnavailable(EquityCurveArgument))
        {
            writer.WriteStartObject("equityCurve");
            writer.WriteString("resolution", resolution.ToString());
            writer.WriteNumber("pointCount", _curve.Length);
            writer.WriteStartArray("points");
            foreach (EquityCurvePoint point in _curve)
            {
                writer.WriteStartObject();
                writer.WriteString("date", ToolJson.FormatDate(point.Timestamp));
                writer.WriteNumber("equity", point.Equity);
                ToolJson.WriteNullableNumber(writer, "drawdownRatio", point.DrawdownRatio);
                if (point.DrawdownReason is { } reason)
                {
                    writer.WriteString("reason", reason);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (_request.EpisodeCount > 0 && !IsUnavailable(DrawdownEpisodesArgument))
        {
            writer.WriteStartArray("drawdownEpisodes");
            foreach (RankedDrawdownEpisode episode in _episodes)
            {
                writer.WriteStartObject();
                writer.WriteNumber("rank", episode.Rank);
                writer.WriteString("peakDate", ToolJson.FormatDate(episode.PeakUtc));
                writer.WriteString("troughDate", ToolJson.FormatDate(episode.TroughUtc));
                if (episode.RecoveryUtc is { } recovery)
                {
                    writer.WriteString("recoveryDate", ToolJson.FormatDate(recovery));
                }
                else
                {
                    writer.WriteNull("recoveryDate");
                }

                writer.WriteNumber("peakEquity", episode.PeakEquity);
                writer.WriteNumber("troughEquity", episode.TroughEquity);
                writer.WriteNumber("depthRatio", episode.DepthRatio);
                writer.WriteNumber("declineBars", episode.DeclineBars);
                ToolJson.WriteNullableNumber(writer, "recoveryBars", episode.RecoveryBars);
                ToolJson.WriteNullableNumber(writer, "underwaterBars", episode.UnderwaterBars);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (_request.PeriodicReturns && !IsUnavailable(PeriodicReturnsArgument))
        {
            writer.WriteStartObject("periodicReturns");
            WritePeriods(writer, "monthly", _periodic.Monthly);
            WritePeriods(writer, "yearly", _periodic.Yearly);
            writer.WriteEndObject();
        }

        if (!_unavailable.IsEmpty)
        {
            writer.WriteStartArray(UnavailableKey);
            foreach ((string part, string reason) in _unavailable)
            {
                writer.WriteStartObject();
                writer.WriteString("part", part);
                writer.WriteString("reason", reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
    }

    private static void WritePeriods(Utf8JsonWriter writer, string name, ImmutableArray<PeriodicReturn> periods)
    {
        writer.WriteStartArray(name);
        foreach (PeriodicReturn period in periods)
        {
            writer.WriteStartObject();
            writer.WriteNumber("year", period.Year);
            if (period.Month is { } month)
            {
                writer.WriteNumber("month", month);
            }

            ToolJson.WriteNullableNumber(writer, "returnRatio", period.ReturnRatio);
            writer.WriteBoolean("isPartial", period.IsPartial);
            if (period.Reason is { } reason)
            {
                writer.WriteString("reason", reason);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
