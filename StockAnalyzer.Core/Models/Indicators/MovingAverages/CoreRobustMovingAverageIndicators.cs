using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

/// <summary>
/// Trimmed moving average: the mean of the last <see cref="CoreWindowPriceAverageIndicatorBase.Period"/> prices after <c>floor(Alpha x Period)</c> values are cut at each end of the sorted window
/// (<see cref="DecimalRobustLocationMath"/>, pure decimal; the DSL <c>trimmed_mean</c> in decimal). Unit: price.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TrimmedMA)]
public class CoreTrimmedMaIndicator : CoreWindowPriceAverageIndicatorBase
{
    public decimal Alpha { get; set; } = IndicatorDefaultConstants.TrimFractionDefaultAlpha;

    public override string Name => $"Trimmed MA ({Period}, {Alpha})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreTrimmedMaParameter p)
        {
            p.Validate();
            Period = p.Period;
            Alpha = p.Alpha;
        }
    }

    protected override CoreWindowAverageParameterBase CreateParameters() => new CoreTrimmedMaParameter { Period = Period, Alpha = Alpha };

    protected override decimal? Compute(ReadOnlySpan<decimal> window)
    {
        Span<decimal> scratch = stackalloc decimal[window.Length];
        MathResult<decimal> result = DecimalRobustLocationMath.TrimmedMean(window, Alpha, scratch);
        return result.IsOk ? result.Value : null;
    }
}

/// <summary>
/// Winsorized moving average: the mean of the last <see cref="CoreWindowPriceAverageIndicatorBase.Period"/> prices with the <c>floor(Alpha x Period)</c> lowest and highest values replaced by the
/// nearest kept value (<see cref="DecimalRobustLocationMath"/>, pure decimal; the DSL <c>winsor_mean</c> in decimal). Unit: price.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.WinsorizedMA)]
public class CoreWinsorizedMaIndicator : CoreWindowPriceAverageIndicatorBase
{
    public decimal Alpha { get; set; } = IndicatorDefaultConstants.TrimFractionDefaultAlpha;

    public override string Name => $"Winsorized MA ({Period}, {Alpha})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreWinsorizedMaParameter p)
        {
            p.Validate();
            Period = p.Period;
            Alpha = p.Alpha;
        }
    }

    protected override CoreWindowAverageParameterBase CreateParameters() => new CoreWinsorizedMaParameter { Period = Period, Alpha = Alpha };

    protected override decimal? Compute(ReadOnlySpan<decimal> window)
    {
        Span<decimal> scratch = stackalloc decimal[window.Length];
        MathResult<decimal> result = DecimalRobustLocationMath.WinsorMean(window, Alpha, scratch);
        return result.IsOk ? result.Value : null;
    }
}

/// <summary>
/// Huber moving average: the Huber M-estimate of location of the last <see cref="CoreWindowPriceAverageIndicatorBase.Period"/> prices (<see cref="DslRobustStatisticsMath.HuberLocation"/>, iteratively
/// reweighted least squares from the median with the MAD x 1.4826 scale; at most 64 iterations, a window that does not converge has no value). The prices are converted to double for the iteration
/// and the result is narrowed once. Every bar gets a fresh execution budget. Unit: price.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HuberMA)]
public class CoreHuberMaIndicator : CoreWindowPriceAverageIndicatorBase
{
    public decimal TuningK { get; set; } = IndicatorDefaultConstants.HuberMaDefaultTuningK;

    public override string Name => $"Huber MA ({Period}, {TuningK})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHuberMaParameter p)
        {
            p.Validate();
            Period = p.Period;
            TuningK = p.TuningK;
        }
    }

    protected override CoreWindowAverageParameterBase CreateParameters() => new CoreHuberMaParameter { Period = Period, TuningK = TuningK };

    protected override decimal? Compute(ReadOnlySpan<decimal> window)
    {
        Span<double> values = stackalloc double[window.Length];
        for (int i = 0; i < window.Length; i++)
        {
            values[i] = (double)window[i];
        }

        var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        MathResult<double> result = DslRobustStatisticsMath.HuberLocation(values, ref budget, (double)TuningK);
        return result.IsOk ? IndicatorCalculationHelper.ToDecimalOrNull(result.Value) : null;
    }
}

/// <summary>
/// Biweight moving average: the Tukey biweight M-estimate of location of the last <see cref="CoreWindowPriceAverageIndicatorBase.Period"/> prices (<see cref="DslRobustStatisticsMath.BiweightLocation"/>,
/// iteratively reweighted from the median with the MAD x 1.4826 scale; prices beyond the cut-off get weight zero). The prices are converted to double for the iteration and the result is narrowed
/// once. Every bar gets a fresh execution budget. Unit: price.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.BiweightMA)]
public class CoreBiweightMaIndicator : CoreWindowPriceAverageIndicatorBase
{
    public decimal TuningC { get; set; } = IndicatorDefaultConstants.BiweightMaDefaultTuningC;

    public override string Name => $"Biweight MA ({Period}, {TuningC})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreBiweightMaParameter p)
        {
            p.Validate();
            Period = p.Period;
            TuningC = p.TuningC;
        }
    }

    protected override CoreWindowAverageParameterBase CreateParameters() => new CoreBiweightMaParameter { Period = Period, TuningC = TuningC };

    protected override decimal? Compute(ReadOnlySpan<decimal> window)
    {
        Span<double> values = stackalloc double[window.Length];
        for (int i = 0; i < window.Length; i++)
        {
            values[i] = (double)window[i];
        }

        var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        MathResult<double> result = DslRobustStatisticsMath.BiweightLocation(values, ref budget, (double)TuningC);
        return result.IsOk ? IndicatorCalculationHelper.ToDecimalOrNull(result.Value) : null;
    }
}

/// <summary>
/// Geometric moving average: <c>exp(mean(ln P))</c> of the last <see cref="CoreWindowPriceAverageIndicatorBase.Period"/> prices (<see cref="DslStatisticsMath.GeometricMean"/>, decimal in and out).
/// Null where a price of the window is not positive. Unit: price.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.GeometricMA)]
public class CoreGeometricMaIndicator : CoreWindowPriceAverageIndicatorBase
{
    public override string Name => $"Geometric MA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreGeometricMaParameter p)
        {
            p.Validate();
            Period = p.Period;
        }
    }

    protected override CoreWindowAverageParameterBase CreateParameters() => new CoreGeometricMaParameter { Period = Period };

    protected override decimal? Compute(ReadOnlySpan<decimal> window)
    {
        MathResult<decimal> result = DslStatisticsMath.GeometricMean(window);
        return result.IsOk ? result.Value : null;
    }
}

/// <summary>
/// Harmonic moving average: <c>n / sum(1 / P)</c> of the last <see cref="CoreWindowPriceAverageIndicatorBase.Period"/> prices (<see cref="DslStatisticsMath.HarmonicMean"/>, decimal in and out).
/// Null where a price of the window is not positive. Unit: price.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HarmonicMA)]
public class CoreHarmonicMaIndicator : CoreWindowPriceAverageIndicatorBase
{
    public override string Name => $"Harmonic MA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHarmonicMaParameter p)
        {
            p.Validate();
            Period = p.Period;
        }
    }

    protected override CoreWindowAverageParameterBase CreateParameters() => new CoreHarmonicMaParameter { Period = Period };

    protected override decimal? Compute(ReadOnlySpan<decimal> window)
    {
        MathResult<decimal> result = DslStatisticsMath.HarmonicMean(window);
        return result.IsOk ? result.Value : null;
    }
}
