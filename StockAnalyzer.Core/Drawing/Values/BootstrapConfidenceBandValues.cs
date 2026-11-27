using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Why a bootstrap confidence band has no interval (or that it has).</summary>
public enum BootstrapBandStatus
{
    /// <summary>The interval was computed.</summary>
    Ok,

    /// <summary>No bar lies between the two anchor times.</summary>
    NoRange,

    /// <summary>The series has fewer than <see cref="BootstrapConfidenceBandValues.MinSamples"/> samples (a price statistic needs that many bars, a return statistic one bar more).</summary>
    TooFewSamples,

    /// <summary><c>Resamples x (1 - ConfidenceLevel)</c> is below the kernel's tail resolution (2): the interval would rest on a single resample.</summary>
    TailResolution,

    /// <summary>The kernel refused the series (a close that is not above zero for a return, a non-finite value).</summary>
    Failed,
}

/// <summary>The result of one bootstrap confidence interval over a drawing range.</summary>
public sealed class BootstrapBand
{
    /// <summary>A band of a range that has no bar.</summary>
    public static readonly BootstrapBand Empty = new(BootstrapBandStatus.NoRange, BootstrapStatKind.Mean, 0, 0, 0, 0, 0, 0, 0m, 0m, 0m, 0m, default, default);

    public BootstrapBand(
        BootstrapBandStatus status,
        BootstrapStatKind kind,
        int rangeBars,
        int effectiveBars,
        int sampleCount,
        int resamples,
        int blockLength,
        decimal confidenceLevel,
        decimal estimate,
        decimal lower,
        decimal upper,
        decimal standardError,
        DateTime windowStart,
        DateTime windowEnd)
    {
        Status = status;
        Kind = kind;
        RangeBars = rangeBars;
        EffectiveBars = effectiveBars;
        SampleCount = sampleCount;
        Resamples = resamples;
        BlockLength = blockLength;
        ConfidenceLevel = confidenceLevel;
        Estimate = estimate;
        Lower = lower;
        Upper = upper;
        StandardError = standardError;
        WindowStart = windowStart;
        WindowEnd = windowEnd;
    }

    public BootstrapBandStatus Status { get; }

    public BootstrapStatKind Kind { get; }

    /// <summary>The number of bars between the two anchors (both ends included).</summary>
    public int RangeBars { get; }

    /// <summary>The number of bars the series was built from (the newest ones when the range is longer than the kernel takes); 0 when nothing was tried.</summary>
    public int EffectiveBars { get; }

    /// <summary>The length of the series the kernel got: the closes (Mean, Median) or the simple returns (StdDev, Sharpe).</summary>
    public int SampleCount { get; }

    public int Resamples { get; }

    /// <summary>The block length the kernel used: the setting, at most half the series.</summary>
    public int BlockLength { get; }

    public decimal ConfidenceLevel { get; }

    public decimal Estimate { get; }

    public decimal Lower { get; }

    public decimal Upper { get; }

    public decimal StandardError { get; }

    /// <summary>The time of the first bar the series was built from.</summary>
    public DateTime WindowStart { get; }

    /// <summary>The time of the last bar of the range.</summary>
    public DateTime WindowEnd { get; }

    public bool IsOk => Status == BootstrapBandStatus.Ok;

    /// <summary>True when the range was longer than the kernel takes and only its newest bars were used.</summary>
    public bool IsTruncated => EffectiveBars > 0 && EffectiveBars < RangeBars;

    /// <summary>True when the interval is a price level (Mean, Median) that the chart can draw as horizontal lines; false for a dimensionless return statistic.</summary>
    public bool IsPriceLevel => BootstrapConfidenceBandValues.IsPriceLevel(Kind);
}

/// <summary>
/// The bootstrap confidence band of a drawing range (the single definition). The interval comes from <see cref="DslResamplingMath.BootConfidenceInterval"/>, unchanged, with a fixed
/// seed (<see cref="Seed"/>) so the same range always gives the same interval. The input series follows the statistic (DX-7): Mean and Median use the CLOSES of the range (price units, the
/// band is drawn as horizontal lines); StdDev and Sharpe use the SIMPLE RETURNS built from the closes inside the range only (dimensionless, the band is a label; the bar before the range is
/// never read, so the series has one sample fewer than the range has bars). The kernel takes 10 .. 512 samples: a price range needs 10 .. 512 bars, a return range 11 .. 513 bars; a longer
/// range uses its newest bars and says so. The block length is the setting, at most half the series.
/// </summary>
public static class BootstrapConfidenceBandValues
{
    /// <summary>The seed of every bootstrap of this tool.</summary>
    public const ulong Seed = 42;

    /// <summary>The fewest samples the kernel takes.</summary>
    public const int MinSamples = DslResamplingMath.MinSeriesLength;

    /// <summary>The most samples the kernel takes.</summary>
    public const int MaxSamples = DslResamplingMath.MaxSeriesLength;

    public const int MinResamples = DslResamplingMath.MinResamples;

    public const int MaxResamples = DslResamplingMath.MaxResamples;

    public const int DefaultResamples = 200;

    public const int MinBlockLength = 1;

    public const int MaxBlockLength = 20;

    public const int DefaultBlockLength = 5;

    public const decimal MinConfidenceLevel = 0.80m;

    public const decimal MaxConfidenceLevel = 0.99m;

    public const decimal DefaultConfidenceLevel = 0.95m;

    /// <summary>The smallest <c>Resamples x (1 - ConfidenceLevel)</c> the kernel takes.</summary>
    public const decimal MinTailResolutionProduct = (decimal)DslResamplingMath.MinTailResolutionProduct;

    private const string EstimateKey = "BootEstimate";
    private const string LowerKey = "BootLower";
    private const string UpperKey = "BootUpper";
    private const string StandardErrorKey = "BootStandardError";
    private const string ConfidenceKey = "BootConfidence";
    private const string ResamplesKey = "BootResamples";
    private const string BlockLengthKey = "BootBlockLength";
    private const string SampleCountKey = "SampleCount";
    private const string RangeBarsKey = "RangeBars";
    private const string EffectiveBarsKey = "EffectiveBars";
    private const string StateKey = "State";
    private const string PriceFormat = "F3";
    private const string ReturnFormat = "F6";
    private const string ConfidenceFormat = "P0";

    /// <summary>True for the statistics measured in price units (Mean, Median).</summary>
    public static bool IsPriceLevel(BootstrapStatKind kind) => kind is BootstrapStatKind.Mean or BootstrapStatKind.Median;

    /// <summary>The unit of the input series of <paramref name="kind"/> ("Price" or "Return").</summary>
    public static string UnitName(BootstrapStatKind kind) => IsPriceLevel(kind) ? "Price" : "Return";

    /// <summary>The fewest bars a range needs for <paramref name="kind"/> (a return series is one sample shorter than its bars).</summary>
    public static int MinBars(BootstrapStatKind kind) => IsPriceLevel(kind) ? MinSamples : MinSamples + 1;

    /// <summary>The most bars one bootstrap takes for <paramref name="kind"/>.</summary>
    public static int MaxBars(BootstrapStatKind kind) => IsPriceLevel(kind) ? MaxSamples : MaxSamples + 1;

    /// <summary>The state text of a band without an interval; empty for <see cref="BootstrapBandStatus.Ok"/>.</summary>
    public static string StatusText(BootstrapBandStatus status) => status switch
    {
        BootstrapBandStatus.Ok => string.Empty,
        BootstrapBandStatus.NoRange => "No bars in the range",
        BootstrapBandStatus.TooFewSamples => string.Create(DrawingValueFormat.Culture, $"Insufficient samples (< {MinSamples})"),
        BootstrapBandStatus.TailResolution => "Increase Resamples or lower Confidence",
        _ => "Bootstrap failed",
    };

    /// <summary>The badge of a band built from the newest bars only (R-18); empty when the whole range was used.</summary>
    public static string TruncationBadge(BootstrapBand band)
        => band.IsTruncated
            ? string.Create(DrawingValueFormat.Culture, $"Last {band.SampleCount} {(band.IsPriceLevel ? "bars" : "returns")}")
            : string.Empty;

    /// <summary>The one-line label of a band: the statistic, its unit, the confidence level and the interval (the text a return statistic is read from).</summary>
    public static string Label(BootstrapBand band)
    {
        if (!band.IsOk)
        {
            return StatusText(band.Status);
        }

        var culture = DrawingValueFormat.Culture;
        string format = band.IsPriceLevel ? PriceFormat : ReturnFormat;
        string text = string.Create(
            culture,
            $"{band.Kind} ({UnitName(band.Kind)}) {band.ConfidenceLevel.ToString(ConfidenceFormat, culture)} CI: {band.Estimate.ToString(format, culture)} [{band.Lower.ToString(format, culture)}, {band.Upper.ToString(format, culture)}]");
        string badge = TruncationBadge(band);
        return badge.Length == 0 ? text : string.Create(culture, $"{text} · {badge}");
    }

    /// <summary>
    /// Bootstraps the statistic <paramref name="kind"/> over the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/> of <paramref name="candles"/> (oldest first).
    /// The settings are clamped to their ranges; a combination the kernel would refuse (<c>Resamples x (1 - ConfidenceLevel) &lt; 2</c>) is reported as
    /// <see cref="BootstrapBandStatus.TailResolution"/> and never changed silently.
    /// </summary>
    public static BootstrapBand Calculate(
        IReadOnlyList<CoreCandleData> candles,
        int firstIndex,
        int lastIndex,
        BootstrapStatKind kind,
        decimal confidenceLevel,
        int resamples,
        int blockLength,
        BootstrapBlockMode blockMode)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return BootstrapBand.Empty;
        }

        decimal confidence = Math.Clamp(confidenceLevel, MinConfidenceLevel, MaxConfidenceLevel);
        int count = Math.Clamp(resamples, MinResamples, MaxResamples);
        int requestedBlock = Math.Clamp(blockLength, MinBlockLength, MaxBlockLength);
        bool priceLevel = IsPriceLevel(kind);
        int rangeBars = lastIndex - firstIndex + 1;
        int bars = Math.Min(rangeBars, MaxBars(kind));
        DateTime windowEnd = candles[lastIndex].Timestamp;
        if (bars < MinBars(kind))
        {
            return Failed(BootstrapBandStatus.TooFewSamples, kind, rangeBars, 0, 0, count, 0, confidence, windowEnd, windowEnd);
        }

        int firstBar = lastIndex - bars + 1;
        DateTime windowStart = candles[firstBar].Timestamp;
        int samples = priceLevel ? bars : bars - 1;
        int block = Math.Min(requestedBlock, samples / 2);
        if (count * (1m - confidence) < MinTailResolutionProduct)
        {
            return Failed(BootstrapBandStatus.TailResolution, kind, rangeBars, bars, samples, count, block, confidence, windowStart, windowEnd);
        }

        var series = new decimal[samples];
        if (priceLevel)
        {
            for (int i = 0; i < samples; i++)
            {
                series[i] = candles[firstBar + i].Close;
            }
        }
        else
        {
            for (int i = 0; i < samples; i++)
            {
                decimal previous = candles[firstBar + i].Close;
                if (previous <= 0m)
                {
                    return Failed(BootstrapBandStatus.Failed, kind, rangeBars, bars, samples, count, block, confidence, windowStart, windowEnd);
                }

                series[i] = (candles[firstBar + i + 1].Close - previous) / previous;
            }
        }

        // One budget per recalculation (1 operation per resample; the default limit of the calculator is far above the 1000 resamples maximum).
        DslExecutionBudget budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        double alpha = (double)(1m - confidence);
        MathResult<BootstrapResult> result = DslResamplingMath.BootConfidenceInterval(series, kind, count, block, alpha, Seed, blockMode, ref budget);
        if (!result.IsOk)
        {
            return Failed(BootstrapBandStatus.Failed, kind, rangeBars, bars, samples, count, block, confidence, windowStart, windowEnd);
        }

        BootstrapResult value = result.Value;
        return new BootstrapBand(
            BootstrapBandStatus.Ok,
            kind,
            rangeBars,
            bars,
            samples,
            count,
            block,
            confidence,
            value.Estimate,
            value.Lower,
            value.Upper,
            value.StandardError,
            windowStart,
            windowEnd);
    }

    /// <summary>The Data-tab values of a band (independent of the evaluation time).</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(BootstrapBand band, IndicatorColor color)
    {
        if (band.Status == BootstrapBandStatus.NoRange)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var culture = DrawingValueFormat.Culture;
        var rows = new List<DrawingCalculatedValue>(10);
        if (!band.IsOk)
        {
            rows.Add(new DrawingCalculatedValue(StateKey, "State", null, StatusText(band.Status), IndicatorColor.Gray));
        }
        else
        {
            string format = band.IsPriceLevel ? PriceFormat : ReturnFormat;
            string unit = UnitName(band.Kind);
            rows.Add(new DrawingCalculatedValue(EstimateKey, string.Create(culture, $"{band.Kind} ({unit})"), band.Estimate, band.Estimate.ToString(format, culture), color));
            rows.Add(new DrawingCalculatedValue(LowerKey, "Lower Bound", band.Lower, band.Lower.ToString(format, culture), color));
            rows.Add(new DrawingCalculatedValue(UpperKey, "Upper Bound", band.Upper, band.Upper.ToString(format, culture), color));
            rows.Add(new DrawingCalculatedValue(StandardErrorKey, "Standard Error", band.StandardError, band.StandardError.ToString(format, culture), IndicatorColor.Gray));
            rows.Add(new DrawingCalculatedValue(ConfidenceKey, "Confidence Level", band.ConfidenceLevel, band.ConfidenceLevel.ToString(ConfidenceFormat, culture), IndicatorColor.Gray));
            rows.Add(new DrawingCalculatedValue(ResamplesKey, "Resamples", band.Resamples, band.Resamples.ToString(culture), IndicatorColor.Gray));
            rows.Add(new DrawingCalculatedValue(BlockLengthKey, "Block Length", band.BlockLength, band.BlockLength.ToString(culture), IndicatorColor.Gray));
        }

        rows.Add(new DrawingCalculatedValue(SampleCountKey, "Samples", band.SampleCount, band.SampleCount.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(RangeBarsKey, "Range Bars", band.RangeBars, band.RangeBars.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(EffectiveBarsKey, "Effective Bars", band.EffectiveBars, band.EffectiveBars.ToString(culture), IndicatorColor.Gray));
        return rows;
    }

    private static BootstrapBand Failed(
        BootstrapBandStatus status,
        BootstrapStatKind kind,
        int rangeBars,
        int effectiveBars,
        int samples,
        int resamples,
        int block,
        decimal confidence,
        DateTime windowStart,
        DateTime windowEnd)
        => new(status, kind, rangeBars, effectiveBars, samples, resamples, block, confidence, 0m, 0m, 0m, 0m, windowStart, windowEnd);
}
