using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Attributes;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Bayesian Online Changepoint Detection (BOCPD, Adams &amp; MacKay 2007) with two-sided recursive CUSUM.
/// <c>Main</c> outputs short-run changepoint probability P(r_t &lt;= 5 | x_{1:t}) bounded in [0, 1];
/// companion series expose <c>ExpectedRunLength</c>, <c>MapRunLength</c>, <c>CusumUp</c>, and <c>CusumDown</c>.
/// Causal: uses only bars &lt;= t.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Bocpd)]
public class CoreBocpdIndicator : CoreIndicatorBase
{
    public const string ExpectedRunLengthSeriesName = nameof(ExpectedRunLength);
    public const string MapRunLengthSeriesName = nameof(MapRunLength);
    public const string CusumUpSeriesName = nameof(CusumUp);
    public const string CusumDownSeriesName = nameof(CusumDown);

    public override string Name => $"BOCPD Regime ({WarmupPeriod})";

    public override bool IsOverlay => false;

    public double HazardLambda { get; set; } = IndicatorDefaultConstants.BocpdDefaultHazardLambda;

    public int MaxRunLength { get; set; } = IndicatorDefaultConstants.BocpdDefaultMaxRunLength;

    public int WarmupPeriod { get; set; } = IndicatorDefaultConstants.BocpdDefaultWarmupPeriod;

    public double CusumDrift { get; set; } = IndicatorDefaultConstants.BocpdDefaultCusumDrift;

    public double CusumThreshold { get; set; } = IndicatorDefaultConstants.BocpdDefaultCusumThreshold;

    [IndicatorResultIgnore]
    public List<decimal?> ExpectedRunLength { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> MapRunLength { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> CusumUp { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> CusumDown { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreBocpdParameter p)
        {
            p.Validate();
            HazardLambda = p.HazardLambda;
            MaxRunLength = p.MaxRunLength;
            WarmupPeriod = p.WarmupPeriod;
            CusumDrift = p.CusumDrift;
            CusumThreshold = p.CusumThreshold;
            PriceSource = p.PriceSource;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        ExpectedRunLength.Clear();
        MapRunLength.Clear();
        CusumUp.Clear();
        CusumDown.Clear();

        var parameters = new CoreBocpdParameter
        {
            HazardLambda = HazardLambda,
            MaxRunLength = MaxRunLength,
            WarmupPeriod = WarmupPeriod,
            CusumDrift = CusumDrift,
            CusumThreshold = CusumThreshold,
            PriceSource = PriceSource
        };

        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        if (candles == null || candles.Count == 0)
        {
            return CreateMultiSeriesResult();
        }

        var returnWindow = new LogReturnWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        BocpdMath.Compute(
            returnWindow.Returns,
            returnWindow.IsValid,
            WarmupPeriod,
            HazardLambda,
            MaxRunLength,
            CusumDrift,
            CusumThreshold,
            _values,
            ExpectedRunLength,
            MapRunLength,
            CusumUp,
            CusumDown);

        return CreateMultiSeriesResult();
    }

    private IIndicatorResult CreateMultiSeriesResult() =>
        IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [IndicatorResult.MainSeriesName] = _values,
            [ExpectedRunLengthSeriesName] = ExpectedRunLength,
            [MapRunLengthSeriesName] = MapRunLength,
            [CusumUpSeriesName] = CusumUp,
            [CusumDownSeriesName] = CusumDown
        });
}
