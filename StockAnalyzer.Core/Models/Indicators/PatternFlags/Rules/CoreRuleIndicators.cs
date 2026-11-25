using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Rules;

/// <summary>
/// Common Group B multi-rule base for <see cref="CoreGranvilleBuySignalIndicator"/> and <see cref="CoreGranvilleSellSignalIndicator"/>
/// (integration spec 4.2, plan R-9/R-12). Exposes <c>Main</c>, <c>RuleCode</c> (both hidden from chart and Data tab), and 4 independent visible rule series.
/// </summary>
public abstract class CoreGranvilleIndicatorBase : CorePatternFlagFullSeriesIndicatorBase
{
    public const string RuleCodeSeriesName = "RuleCode";

    private readonly bool _buy;

    protected CoreGranvilleIndicatorBase(IndicatorType type, bool buy)
        : base(PatternFlagIndicatorCatalog.Get(type))
    {
        _buy = buy;
    }

    protected CoreGranvilleSignalParameter Parameter { get; private set; } = new();

    protected List<decimal?> RuleCodeValues { get; } = new();
    protected List<decimal?> Rule1Values { get; } = new();
    protected List<decimal?> Rule2Values { get; } = new();
    protected List<decimal?> Rule3Values { get; } = new();
    protected List<decimal?> Rule4Values { get; } = new();

    /// <summary>The <c>Main</c> series (0m / 1m) under its own name (hidden on the chart and in the Data tab).</summary>
    public IReadOnlyList<decimal?> Main => _values;

    /// <summary>Priority rule code (<c>0, +1..+4</c> for Buy; <c>0, -1..-4</c> for Sell; hidden on the chart and in the Data tab).</summary>
    public IReadOnlyList<decimal?> RuleCode => RuleCodeValues;

    protected override int MinBars => Parameter.MaPeriod + Parameter.SlopePeriod + 1;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        base.Configure(parameters);
        if (parameters is CoreGranvilleSignalParameter p)
        {
            Parameter = p;
        }
    }

    protected sealed override void FillSeries(CandleData[] bars, int count, int minBars, decimal?[] main, decimal?[]? direction, decimal?[]? bullish, decimal?[]? bearish, CancellationToken cancellationToken)
        => throw new System.NotSupportedException($"{Spec.Type} populates multi-rule series directly in CalculateCore.");

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        => CalculateCore(candles, CancellationToken.None);

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles, CancellationToken cancellationToken)
    {
        int count = candles.Count;
        int minBars = MinBars;
        string[] ruleNames = Spec.RuleSeries!;

        var main = new decimal?[count];
        var ruleCode = new decimal?[count];
        var r1 = new decimal?[count];
        var r2 = new decimal?[count];
        var r3 = new decimal?[count];
        var r4 = new decimal?[count];

        CandleData[] bars = ArrayPool<CandleData>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                CoreCandleData c = candles[i];
                bars[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
            }

            var context = new PatternSeriesContext(bars, count);
            GranvilleBarResult[] results = _buy
                ? GranvilleSignalEngine.RunBuy(context, Parameter, cancellationToken)
                : GranvilleSignalEngine.RunSell(context, Parameter, cancellationToken);

            for (int t = 0; t < count; t++)
            {
                if (t + 1 < minBars)
                {
                    continue;
                }

                GranvilleBarResult res = results[t];
                main[t] = res.Main ? 1m : 0m;
                ruleCode[t] = res.RuleCode;
                r1[t] = res.Rule1 ? 1m : 0m;
                r2[t] = res.Rule2 ? 1m : 0m;
                r3[t] = res.Rule3 ? 1m : 0m;
                r4[t] = res.Rule4 ? 1m : 0m;
            }
        }
        finally
        {
            ArrayPool<CandleData>.Shared.Return(bars);
        }

        _values.AddRange(main);

        RuleCodeValues.Clear();
        Rule1Values.Clear();
        Rule2Values.Clear();
        Rule3Values.Clear();
        Rule4Values.Clear();
        RuleCodeValues.AddRange(ruleCode);
        Rule1Values.AddRange(r1);
        Rule2Values.AddRange(r2);
        Rule3Values.AddRange(r3);
        Rule4Values.AddRange(r4);

        var series = new Dictionary<string, IReadOnlyList<decimal?>>(6)
        {
            [IndicatorResult.MainSeriesName] = main,
            [RuleCodeSeriesName] = ruleCode,
            [ruleNames[0]] = r1,
            [ruleNames[1]] = r2,
            [ruleNames[2]] = r3,
            [ruleNames[3]] = r4,
        };

        return IndicatorResult.Success(series);
    }
}

/// <summary>Granville's Law Buy Signal (rules 1–4) evaluated by <see cref="GranvilleSignalEngine.RunBuy"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.GranvilleBuySignal)]
public sealed class CoreGranvilleBuySignalIndicator : CoreGranvilleIndicatorBase
{
    public CoreGranvilleBuySignalIndicator()
        : base(IndicatorType.GranvilleBuySignal, buy: true)
    {
    }

    /// <summary>Buy Rule 1 (New Buy): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Buy1 => Rule1Values;

    /// <summary>Buy Rule 2 (Pullback Buy): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Buy2 => Rule2Values;

    /// <summary>Buy Rule 3 (Bounce Buy): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Buy3 => Rule3Values;

    /// <summary>Buy Rule 4 (Reversal Buy): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Buy4 => Rule4Values;
}

/// <summary>Granville's Law Sell Signal (rules 1–4) evaluated by <see cref="GranvilleSignalEngine.RunSell"/>.</summary>
[StockAnalyzerIndicator(IndicatorType.GranvilleSellSignal)]
public sealed class CoreGranvilleSellSignalIndicator : CoreGranvilleIndicatorBase
{
    public CoreGranvilleSellSignalIndicator()
        : base(IndicatorType.GranvilleSellSignal, buy: false)
    {
    }

    /// <summary>Sell Rule 1 (New Sell): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Sell1 => Rule1Values;

    /// <summary>Sell Rule 2 (Return Sell): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Sell2 => Rule2Values;

    /// <summary>Sell Rule 3 (Rejection Sell): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Sell3 => Rule3Values;

    /// <summary>Sell Rule 4 (Reversal Sell): +1m on active bars.</summary>
    public IReadOnlyList<decimal?> Sell4 => Rule4Values;
}
