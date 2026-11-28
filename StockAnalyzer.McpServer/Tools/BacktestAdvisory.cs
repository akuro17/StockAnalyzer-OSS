using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Shared advisory diagnostics for MCP backtest and screener tools (<c>SIZING_NOTIONAL_SMALL</c>, <c>SAMPLE_TOO_SMALL</c>,
/// <c>ZERO_FRICTION_ASSUMPTIONS</c>, <c>PRICE_LIMIT_ALWAYS_FILL</c>, <c>PRICE_RETURN_EXCLUDES_DIVIDENDS</c>, and <c>SURVIVORSHIP_BIAS_UNIVERSE</c>).
/// Appends additive warnings/disclosures without altering any Core calculation, metric, or factory default.
/// </summary>
internal static class BacktestAdvisory
{
    public const string SizingNotionalSmallCode = "SIZING_NOTIONAL_SMALL";
    public const string SampleTooSmallCode = "SAMPLE_TOO_SMALL";
    public const string ZeroFrictionAssumptionsCode = "ZERO_FRICTION_ASSUMPTIONS";
    public const string PriceLimitAlwaysFillCode = "PRICE_LIMIT_ALWAYS_FILL";
    public const string PriceReturnExcludesDividendsCode = "PRICE_RETURN_EXCLUDES_DIVIDENDS";
    public const string SurvivorshipBiasUniverseCode = "SURVIVORSHIP_BIAS_UNIVERSE";

    public static string? ForZeroFriction(BacktestConfiguration configuration)
    {
        if (configuration.CommissionFlat != 0m || configuration.CommissionPerUnit != 0m || configuration.SlippageRatio != 0m)
        {
            return null;
        }

        return $"{ZeroFrictionAssumptionsCode}: Backtest uses zero commission and zero slippage (commissionFlat=0, commissionPerUnit=0, slippageRatio=0); reported returns may overstate live net performance unless explicit transaction costs are specified.";
    }

    public static string? ForSweepZeroFriction(IReadOnlyList<BoundBacktestSettings> combinations)
    {
        for (int i = 0; i < combinations.Count; i++)
        {
            if (ForZeroFriction(combinations[i].Configuration) is { } warning)
            {
                return warning;
            }
        }

        return null;
    }

    public static string? ForPriceLimitAlwaysFill(BacktestConfiguration configuration)
    {
        if (configuration.PriceLimitFillMode != PriceLimitFillMode.AlwaysFill)
        {
            return null;
        }

        return $"{PriceLimitAlwaysFillCode}: PriceLimitFillMode is AlwaysFill; simulated orders still fill at bar prices even when a bar is locked at an exchange price limit (use RejectIfAtLimitPrice for limit-bar fill rejection).";
    }

    public static string? ForSweepPriceLimitAlwaysFill(IReadOnlyList<BoundBacktestSettings> combinations)
    {
        for (int i = 0; i < combinations.Count; i++)
        {
            if (ForPriceLimitAlwaysFill(combinations[i].Configuration) is { } warning)
            {
                return warning;
            }
        }

        return null;
    }

    public static string ForPriceReturnExcludesDividends()
        => $"{PriceReturnExcludesDividendsCode}: Backtest evaluates split-adjusted price return excluding cash dividends (total return may differ for dividend-paying equities).";

    public static string ForSurvivorshipUniverse()
        => $"{SurvivorshipBiasUniverseCode}: Evaluation uses only symbols present in the local Parquet dataset; delisted symbols removed from disk are excluded (survivorship bias).";

    public static void AppendBacktestAdvisories(IList<string> target, BacktestConfiguration configuration)
    {
        if (ForZeroFriction(configuration) is { } zeroFriction)
        {
            target.Add(zeroFriction);
        }

        if (ForPriceLimitAlwaysFill(configuration) is { } alwaysFill)
        {
            target.Add(alwaysFill);
        }

        target.Add(ForPriceReturnExcludesDividends());
        target.Add(ForSurvivorshipUniverse());
    }

    public static void AppendSweepAdvisories(IList<string> target, IReadOnlyList<BoundBacktestSettings> combinations)
    {
        if (ForSweepZeroFriction(combinations) is { } zeroFriction)
        {
            target.Add(zeroFriction);
        }

        if (ForSweepPriceLimitAlwaysFill(combinations) is { } alwaysFill)
        {
            target.Add(alwaysFill);
        }

        target.Add(ForPriceReturnExcludesDividends());
        target.Add(ForSurvivorshipUniverse());
    }

    public static bool HasExplicitSizingParameter(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(BacktestSettingsBinder.AccountArgument, out JsonElement account)
           && HasExplicitSizingParameter((JsonElement?)account);

    public static bool HasExplicitSizingParameter(JsonElement? accountElement)
        => accountElement is { ValueKind: JsonValueKind.Object } account
           && account.TryGetProperty("sizingParameter", out _);

    public static decimal? HighestCloseInRange(IReadOnlyList<CandleData> stored, DateOnly start, DateOnly end)
    {
        decimal maxClose = 0m;
        bool found = false;
        for (int i = 0; i < stored.Count; i++)
        {
            DateOnly day = DateOnly.FromDateTime(stored[i].Timestamp);
            if (day < start || day > end)
            {
                continue;
            }

            decimal close = stored[i].Close;
            if (!found || close > maxClose)
            {
                maxClose = close;
                found = true;
            }
        }

        return found && maxClose > 0m ? maxClose : null;
    }

    public static decimal? HighestCloseInRanges(IReadOnlyList<CandleData> stored, IReadOnlyList<(DateOnly Start, DateOnly End)> ranges)
    {
        decimal maxClose = 0m;
        bool found = false;
        for (int i = 0; i < stored.Count; i++)
        {
            DateOnly day = DateOnly.FromDateTime(stored[i].Timestamp);
            bool inside = false;
            for (int r = 0; r < ranges.Count; r++)
            {
                if (day >= ranges[r].Start && day <= ranges[r].End)
                {
                    inside = true;
                    break;
                }
            }

            if (!inside)
            {
                continue;
            }

            decimal close = stored[i].Close;
            if (!found || close > maxClose)
            {
                maxClose = close;
                found = true;
            }
        }

        return found && maxClose > 0m ? maxClose : null;
    }

    public static string? ForSingleSizing(
        BacktestConfiguration configuration,
        decimal? highestClose,
        int totalTrades,
        bool sizingParameterExplicit,
        McpLimits limits)
    {
        if (configuration.SizingModel != PositionSizingModel.FixedQuantity
            || totalTrades < 1
            || configuration.InitialCapital <= 0m
            || highestClose is not { } maxClose
            || maxClose <= 0m
            || configuration.SizingParameter < 0m)
        {
            return null;
        }

        if (configuration.SizingParameter > 0m && maxClose > decimal.MaxValue / configuration.SizingParameter)
        {
            return null;
        }

        decimal ratio = configuration.SizingParameter * maxClose / configuration.InitialCapital;
        return FormatSizingWarningIfSmall(ratio, configuration.SizingParameter, sizingParameterExplicit, limits);
    }

    public static string? ForPortfolioSizing(
        BacktestConfiguration configuration,
        IReadOnlyList<decimal> sleeveHighestCloses,
        int pooledTrades,
        bool sizingParameterExplicit,
        McpLimits limits)
    {
        if (configuration.SizingModel != PositionSizingModel.FixedQuantity
            || pooledTrades < 1
            || configuration.InitialCapital <= 0m
            || sleeveHighestCloses.Count == 0
            || configuration.SizingParameter < 0m)
        {
            return null;
        }

        decimal notionalSum = 0m;
        for (int i = 0; i < sleeveHighestCloses.Count; i++)
        {
            decimal close = sleeveHighestCloses[i];
            if (close <= 0m)
            {
                continue;
            }

            if (configuration.SizingParameter > 0m && close > decimal.MaxValue / configuration.SizingParameter)
            {
                return null;
            }

            decimal term = configuration.SizingParameter * close;
            if (notionalSum > decimal.MaxValue - term)
            {
                return null;
            }

            notionalSum += term;
        }

        if (notionalSum <= 0m)
        {
            return null;
        }

        decimal ratio = notionalSum / configuration.InitialCapital;
        return FormatSizingWarningIfSmall(ratio, configuration.SizingParameter, sizingParameterExplicit, limits);
    }

    public static string? ForSweepSizing(
        IReadOnlyList<BoundBacktestSettings> combinations,
        decimal? highestClose,
        bool hasAnyTrade,
        bool sizingParameterExplicit,
        McpLimits limits)
    {
        if (!hasAnyTrade || highestClose is not { } maxClose || maxClose <= 0m)
        {
            return null;
        }

        decimal? maxRatio = null;
        decimal maxRatioSizing = 0m;
        for (int i = 0; i < combinations.Count; i++)
        {
            BacktestConfiguration cfg = combinations[i].Configuration;
            if (cfg.SizingModel != PositionSizingModel.FixedQuantity
                || cfg.InitialCapital <= 0m
                || cfg.SizingParameter < 0m)
            {
                continue;
            }

            if (cfg.SizingParameter > 0m && maxClose > decimal.MaxValue / cfg.SizingParameter)
            {
                return null;
            }

            decimal ratio = cfg.SizingParameter * maxClose / cfg.InitialCapital;
            if (maxRatio is null || ratio > maxRatio.Value)
            {
                maxRatio = ratio;
                maxRatioSizing = cfg.SizingParameter;
            }
        }

        if (maxRatio is null)
        {
            return null;
        }

        return FormatSizingWarningIfSmall(maxRatio.Value, maxRatioSizing, sizingParameterExplicit, limits);
    }

    public static string? ForSingleSampleSize(int evaluatedBarCount, McpLimits limits)
    {
        if (evaluatedBarCount <= 0 || evaluatedBarCount >= limits.BacktestMinBarsForRatioMetrics)
        {
            return null;
        }

        return FormattableString.Invariant(
            $"{SampleTooSmallCode}: Only {evaluatedBarCount} evaluated bar(s) were tested; ratio metrics and bootstrap intervals require at least {limits.BacktestMinBarsForRatioMetrics} evaluated bars.");
    }

    public static string? ForPortfolioSampleSize(
        IReadOnlyList<(string Symbol, int SampleCount)> succeededSleeveSamples,
        McpLimits limits)
    {
        if (succeededSleeveSamples.Count == 0)
        {
            return null;
        }

        int minCount = int.MaxValue;
        string minSymbol = string.Empty;
        for (int i = 0; i < succeededSleeveSamples.Count; i++)
        {
            (string symbol, int count) = succeededSleeveSamples[i];
            if (count > 0 && count < minCount)
            {
                minCount = count;
                minSymbol = symbol;
            }
        }

        if (minCount == int.MaxValue || minCount >= limits.BacktestMinBarsForRatioMetrics)
        {
            return null;
        }

        return FormattableString.Invariant(
            $"{SampleTooSmallCode}: Smallest sleeve {minSymbol} evaluated only {minCount} bar(s); ratio metrics and bootstrap intervals require at least {limits.BacktestMinBarsForRatioMetrics} evaluated bars.");
    }

    public static string? ForOptimizeSampleSize(
        IReadOnlyList<SweepRun> inSample,
        IReadOnlyList<SweepRun> outOfSample,
        McpLimits limits)
    {
        int minCount = int.MaxValue;
        for (int i = 0; i < inSample.Count; i++)
        {
            SweepRun run = inSample[i];
            if (!run.NoEvaluatedBars && run.SampleCount > 0 && run.SampleCount < minCount)
            {
                minCount = run.SampleCount;
            }
        }

        for (int i = 0; i < outOfSample.Count; i++)
        {
            SweepRun run = outOfSample[i];
            if (!run.NoEvaluatedBars && run.SampleCount > 0 && run.SampleCount < minCount)
            {
                minCount = run.SampleCount;
            }
        }

        if (minCount == int.MaxValue || minCount >= limits.BacktestMinBarsForRatioMetrics)
        {
            return null;
        }

        return FormattableString.Invariant(
            $"{SampleTooSmallCode}: Only {minCount} evaluated bar(s) were tested; ratio metrics and bootstrap intervals require at least {limits.BacktestMinBarsForRatioMetrics} evaluated bars.");
    }

    public static string? ForWalkForwardSampleSize(
        IReadOnlyList<(int WindowIndex, int? InSampleCount, int? OutOfSampleCount)> evaluatedWindows,
        McpLimits limits)
    {
        int minCount = int.MaxValue;
        int minWindowIndex = 0;
        string minPart = string.Empty;

        for (int i = 0; i < evaluatedWindows.Count; i++)
        {
            (int windowIndex, int? inSampleCount, int? outOfSampleCount) = evaluatedWindows[i];
            if (inSampleCount is { } ins && ins > 0 && ins < minCount)
            {
                minCount = ins;
                minWindowIndex = windowIndex;
                minPart = "in-sample";
            }

            if (outOfSampleCount is { } oos && oos > 0 && oos < minCount)
            {
                minCount = oos;
                minWindowIndex = windowIndex;
                minPart = "out-of-sample";
            }
        }

        if (minCount == int.MaxValue || minCount >= limits.BacktestMinBarsForRatioMetrics)
        {
            return null;
        }

        return FormattableString.Invariant(
            $"{SampleTooSmallCode}: Window {minWindowIndex} ({minPart}) evaluated only {minCount} bar(s); ratio metrics and bootstrap intervals require at least {limits.BacktestMinBarsForRatioMetrics} evaluated bars.");
    }

    private static string? FormatSizingWarningIfSmall(
        decimal ratio,
        decimal sizingParameter,
        bool sizingParameterExplicit,
        McpLimits limits)
    {
        if (ratio >= limits.BacktestSizingWarnRatio)
        {
            return null;
        }

        string pct = (ratio * 100m).ToString("0.####", CultureInfo.InvariantCulture);
        if (!sizingParameterExplicit)
        {
            return $"{SizingNotionalSmallCode}: The default fixed size of 1 share was used, so the largest possible position is only {pct}% of the initial capital; state a ratio sizing (sizingModel PercentOfEquity) explicitly for meaningful return and risk metrics.";
        }

        string quantityText = sizingParameter.ToString("0.####", CultureInfo.InvariantCulture);
        return $"{SizingNotionalSmallCode}: The fixed size of {quantityText} share(s) makes the largest possible position only {pct}% of the initial capital; state a ratio sizing (sizingModel PercentOfEquity) explicitly for meaningful return and risk metrics.";
    }
}
