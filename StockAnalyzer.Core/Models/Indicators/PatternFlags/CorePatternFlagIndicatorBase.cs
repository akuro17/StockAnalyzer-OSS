using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags;

/// <summary>
/// Base of the 56 chart-pattern flag indicators: outputs <c>Main</c> (null = not computable yet, 0m = no pattern, 1m = pattern) and, for a bidirectional type,
/// <c>Direction</c> (-1m bearish, 0m none, +1m bullish) plus visible named side/rule series.
/// All 56 types use whole-series Model P engines (<see cref="PatternFlagEvaluationModel.FullSeries"/>) and override <see cref="FillSeries"/>.
/// </summary>
public abstract class CorePatternFlagIndicatorBase : CoreIndicatorBase
{
    protected CorePatternFlagIndicatorBase(PatternFlagSpec spec)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
    }

    /// <summary>The row of the catalog table this indicator realises.</summary>
    public PatternFlagSpec Spec { get; }

    /// <summary>
    /// The <c>Direction</c> series of a bidirectional type (-1m, 0m, +1m; null while not computable); empty for a type without direction. A derived class exposes it, the bullish and the bearish
    /// series and a <c>Main</c> alias as public properties under the names of the catalog row, because output-name discovery of the Screener and MCP lists the public series properties.
    /// The lists belong to the last calculation; read the returned result, not the instance, after a call that may have been empty.
    /// </summary>
    protected List<decimal?> DirectionValues { get; } = new();

    /// <summary>The visible bullish series of a bidirectional type: 0m or +1m (null while not computable); empty for a type without direction.</summary>
    protected List<decimal?> BullishValues { get; } = new();

    /// <summary>The visible bearish series of a bidirectional type: 0m or -1m (null while not computable); empty for a type without direction.</summary>
    protected List<decimal?> BearishValues { get; } = new();

    public override string Name => Spec.Type.GetDescription();

    /// <summary>A flag is drawn in its own sub-window.</summary>
    public override bool IsOverlay => false;

    /// <summary>Bars needed before the first non-null value (unit: bars). A type whose need depends on its parameters overrides it.</summary>
    protected virtual int MinBars => Spec.MinBars;

    /// <summary>The direction filter of the type; a type without the setting keeps <see cref="PatternDirectionMode.Both"/>.</summary>
    protected virtual PatternDirectionMode DirectionMode => PatternDirectionMode.Both;

    public override CoreIndicatorSettings GetDefaultSettings() => DefaultCoreIndicatorSettings.CreatePatternFlag(Spec);

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        => CalculateCore(candles, CancellationToken.None);

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles, CancellationToken cancellationToken)
    {
        int count = candles.Count;
        var main = new decimal?[count];
        decimal?[]? direction = Spec.HasDirection ? new decimal?[count] : null;
        decimal?[]? bullish = Spec.HasDirection ? new decimal?[count] : null;
        decimal?[]? bearish = Spec.HasDirection ? new decimal?[count] : null;

        CandleData[] bars = ArrayPool<CandleData>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                CoreCandleData c = candles[i];
                bars[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
            }

            FillSeries(bars, count, MinBars, main, direction, bullish, bearish, cancellationToken);
        }
        finally
        {
            ArrayPool<CandleData>.Shared.Return(bars);
        }

        _values.AddRange(main);

        // The result owns its arrays; the instance lists behind the public series properties are copies, so a later calculation on the same instance never changes an earlier result.
        var series = new Dictionary<string, IReadOnlyList<decimal?>>(4)
        {
            [IndicatorResult.MainSeriesName] = main,
        };

        DirectionValues.Clear();
        BullishValues.Clear();
        BearishValues.Clear();
        if (direction != null && bullish != null && bearish != null)
        {
            DirectionValues.AddRange(direction);
            BullishValues.AddRange(bullish);
            BearishValues.AddRange(bearish);
            series[IndicatorSeriesNames.PatternDirection] = direction;
            series[Spec.BullishSeries!] = bullish;
            series[Spec.BearishSeries!] = bearish;
        }

        return IndicatorResult.Success(series);
    }

    /// <summary>
    /// Fills <paramref name="main"/> (and, when the type has a direction, <paramref name="direction"/> and the two visible side series <paramref name="bullish"/> / <paramref name="bearish"/>) for bars 0..count-1 in
    /// chronological order; <paramref name="bars"/> may be longer than <paramref name="count"/> (pooled array).
    /// </summary>
    protected abstract void FillSeries(CandleData[] bars, int count, int minBars, decimal?[] main, decimal?[]? direction, decimal?[]? bullish, decimal?[]? bearish, CancellationToken cancellationToken);

    /// <summary>Applies <see cref="DirectionMode"/>: a bidirectional pattern of a direction the mode does not allow is "not met" (decision U-7 A: Main and Direction are narrowed together).</summary>
    protected PatternFlagOutcome ApplyDirectionMode(PatternFlagOutcome outcome)
    {
        if (!outcome.IsMet || !Spec.HasDirection)
        {
            return outcome;
        }

        return (DirectionMode, outcome.Direction) switch
        {
            (PatternDirectionMode.BullishOnly, < 0) => PatternFlagOutcome.None,
            (PatternDirectionMode.BearishOnly, > 0) => PatternFlagOutcome.None,
            _ => outcome,
        };
    }

    /// <summary>
    /// Writes the outcome at bar <paramref name="index"/> and enforces the S-1 invariants between Main and Direction. The side series follow the direction (integration spec 4.2): bullish = +1m where the
    /// direction is +1, bearish = -1m where it is -1, 0m elsewhere.
    /// </summary>
    protected void WriteOutcome(PatternFlagOutcome outcome, int index, decimal?[] main, decimal?[]? direction, decimal?[]? bullish, decimal?[]? bearish)
    {
        main[index] = outcome.IsMet ? 1m : 0m;
        if (direction == null)
        {
            return;
        }

        if (outcome.IsMet && outcome.Direction == 0)
        {
            throw new InvalidOperationException($"{Spec.Type} is bidirectional: a met pattern must report Bullish or Bearish (S-1).");
        }

        int sign = outcome.IsMet ? Math.Sign(outcome.Direction) : 0;
        direction[index] = sign;
        WriteSides(index, bullish, bearish, sign > 0, sign < 0);
    }

    /// <summary>Writes the two visible side series of a bidirectional type at <paramref name="index"/>: <c>+1m</c> / <c>0m</c> for the bullish and <c>-1m</c> / <c>0m</c> for the bearish side.</summary>
    protected static void WriteSides(int index, decimal?[]? bullish, decimal?[]? bearish, bool bullishLit, bool bearishLit)
    {
        if (bullish != null)
        {
            bullish[index] = bullishLit ? 1m : 0m;
        }

        if (bearish != null)
        {
            bearish[index] = bearishLit ? -1m : 0m;
        }
    }
}
