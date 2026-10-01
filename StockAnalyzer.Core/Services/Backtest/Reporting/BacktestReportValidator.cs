using System;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Validates the complete persisted/displayed report boundary rather than trusting default struct values.</summary>
public static class BacktestReportValidator
{
    public static void Validate(BacktestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        ValidateMetric(nameof(report.TotalPnL), report.TotalPnL, MetricUnit.Currency);
        ValidateMetric(nameof(report.MaxDrawdownAmount), report.MaxDrawdownAmount, MetricUnit.Currency);
        ValidateMetric(nameof(report.ExpectedPayoff), report.ExpectedPayoff, MetricUnit.Currency);
        ValidateMetric(nameof(report.TotalReturn), report.TotalReturn, MetricUnit.ReturnRatio);
        ValidateMetric(nameof(report.CAGR), report.CAGR, MetricUnit.ReturnRatio);
        ValidateMetric(nameof(report.WinRate), report.WinRate, MetricUnit.WinRateRatio);
        ValidateMetric(nameof(report.ProfitFactor), report.ProfitFactor, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.MaxDrawdown), report.MaxDrawdown, MetricUnit.DrawdownRatio);
        ValidateMetric(nameof(report.UlcerIndex), report.UlcerIndex, MetricUnit.PercentPoints);
        ValidateMetric(nameof(report.BarSharpe), report.BarSharpe, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.AnnualizedSharpe), report.AnnualizedSharpe, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.AnnualizedSharpeAutocorrelationAdjusted), report.AnnualizedSharpeAutocorrelationAdjusted, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.BarSortino), report.BarSortino, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.AnnualizedSortino), report.AnnualizedSortino, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.AnnualizedSortinoAutocorrelationAdjusted), report.AnnualizedSortinoAutocorrelationAdjusted, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.CalmarFullPeriod), report.CalmarFullPeriod, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.SQN), report.SQN, MetricUnit.Dimensionless);
        ValidateMetric(nameof(report.RecoveryFactor), report.RecoveryFactor, MetricUnit.Dimensionless);

        // Extended metrics are optional (absent in reports persisted before they existed); each present one is validated independently.
        foreach ((string name, MetricValue metric, MetricUnit expectedUnit) in report.EnumerateExtendedMetrics())
        {
            ValidateMetric(name, metric, expectedUnit);
        }
        ValidateExtendedMetricValues(report);
        ValidateCensoringFlag(nameof(report.MaxDepthDrawdownDurationRightCensored), report.MaxDepthDrawdownDurationRightCensored, nameof(report.MaxDepthDrawdownDuration), report.MaxDepthDrawdownDuration);
        ValidateCensoringFlag(nameof(report.LongestDrawdownDurationRightCensored), report.LongestDrawdownDurationRightCensored, nameof(report.LongestDrawdownDuration), report.LongestDrawdownDuration);

        bool adjustedSortinoValid = report.AnnualizedSortinoAutocorrelationAdjusted.Status == MetricStatus.Valid;
        if (adjustedSortinoValid != report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval.HasValue)
        {
            throw Invalid("The adjusted Sortino confidence interval must exist if and only if its metric is Valid.");
        }
        if (report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval is { } interval && interval.Lower > interval.Upper)
        {
            throw Invalid("The adjusted Sortino confidence interval lower bound must not exceed its upper bound.");
        }

        if (report.TotalTrades < 0 || report.WinTrades < 0 || report.LossTrades < 0 || report.BreakevenTrades < 0)
        {
            throw Invalid("Trade counts must be non-negative.");
        }
        long classified = (long)report.WinTrades + report.LossTrades + report.BreakevenTrades;
        if (classified != report.TotalTrades)
        {
            throw Invalid("Win, loss, and breakeven counts must sum to TotalTrades.");
        }
        if (!Enum.IsDefined(report.Frame))
        {
            throw Invalid("Frame must be a defined TimeFrame value.");
        }
        if (report.ExecutionModel is { } executionModel && executionModel != ExecutionModel.YFinanceApproximate)
        {
            throw Invalid("ExecutionModel must be YFinanceApproximate when present.");
        }
        string? expectedDisclosure = report.ExecutionModel == ExecutionModel.YFinanceApproximate
            ? BacktestReport.YFinanceApproximateDisclosure
            : null;
        if (report.ExecutionDisclosure != expectedDisclosure)
        {
            throw Invalid("ExecutionDisclosure must match the execution model.");
        }
        if (report.AnnualPeriods is < 1 or > 366)
        {
            throw Invalid("AnnualPeriods must be in [1, 366].");
        }
    }

    private static void ValidateMetric(string name, MetricValue metric, MetricUnit expectedUnit)
    {
        if (!Enum.IsDefined(metric.Status) || !Enum.IsDefined(metric.Unit) || !Enum.IsDefined(metric.Reason))
        {
            throw Invalid($"{name} contains an undefined enum value.");
        }
        if (metric.Unit != expectedUnit)
        {
            throw Invalid($"{name} must use unit {expectedUnit}.");
        }

        bool isValid = metric.Status == MetricStatus.Valid;
        if (isValid != metric.Value.HasValue)
        {
            throw Invalid($"{name} must have a value if and only if its status is Valid.");
        }
        if (isValid && metric.Reason != MetricReason.None)
        {
            throw Invalid($"{name} must use reason None when Valid.");
        }
        if (!isValid && metric.Reason == MetricReason.None)
        {
            throw Invalid($"{name} must provide a reason when non-Valid.");
        }
    }

    /// <summary>
    /// Value-domain and cross-field contracts of the extended metrics. Each contract applies only when the metrics it names are present
    /// (legacy reports carry none) and Valid (a non-Valid metric has no value to constrain). TimeInMarket &lt;= sample bar count is not
    /// checkable here because the sample size is not part of the report.
    /// </summary>
    private static void ValidateExtendedMetricValues(BacktestReport report)
    {
        decimal? grossProfit = ValidValue(report.GrossProfit);
        decimal? grossLoss = ValidValue(report.GrossLoss);
        decimal? averageWin = ValidValue(report.AverageWin);
        decimal? averageLoss = ValidValue(report.AverageLoss);
        decimal? largestWin = ValidValue(report.LargestWin);
        decimal? largestLoss = ValidValue(report.LargestLoss);
        decimal? maxDepthDrawdownDuration = ValidValue(report.MaxDepthDrawdownDuration);
        decimal? longestDrawdownDuration = ValidValue(report.LongestDrawdownDuration);
        decimal? timeInMarket = ValidValue(report.TimeInMarket);
        decimal? exposure = ValidValue(report.Exposure);

        RequireAtLeastZero(nameof(report.GrossProfit), grossProfit);
        RequireAtLeastZero(nameof(report.GrossLoss), grossLoss);
        RequirePositive(nameof(report.AverageWin), averageWin);
        RequirePositive(nameof(report.AverageLoss), averageLoss);
        RequirePositive(nameof(report.PayoffRatio), ValidValue(report.PayoffRatio));
        RequirePositive(nameof(report.LargestWin), largestWin);
        RequirePositive(nameof(report.LargestLoss), largestLoss);
        RequireAtLeastZero(nameof(report.AverageHoldingPeriod), ValidValue(report.AverageHoldingPeriod));

        RequireCount(nameof(report.MaxConsecutiveWins), ValidValue(report.MaxConsecutiveWins), report.WinTrades, nameof(report.WinTrades));
        RequireCount(nameof(report.MaxConsecutiveLosses), ValidValue(report.MaxConsecutiveLosses), report.LossTrades, nameof(report.LossTrades));

        RequireWholeNonNegative(nameof(report.MaxDepthDrawdownDuration), maxDepthDrawdownDuration);
        RequireWholeNonNegative(nameof(report.LongestDrawdownDuration), longestDrawdownDuration);
        if (maxDepthDrawdownDuration is { } depthBars && longestDrawdownDuration is { } longestBars && longestBars < depthBars)
        {
            throw Invalid($"{nameof(report.LongestDrawdownDuration)} must not be shorter than {nameof(report.MaxDepthDrawdownDuration)}.");
        }

        RequireWholeNonNegative(nameof(report.TimeInMarket), timeInMarket);
        if (exposure is { } ratio && (ratio < 0m || ratio > 1m))
        {
            throw Invalid($"{nameof(report.Exposure)} must be within [0, 1].");
        }
        if (timeInMarket is { } barsInMarket && exposure is { } exposureRatio && (barsInMarket == 0m) != (exposureRatio == 0m))
        {
            throw Invalid($"{nameof(report.Exposure)} must be zero if and only if {nameof(report.TimeInMarket)} is zero.");
        }

        RequireOrdered(nameof(report.AverageWin), averageWin, nameof(report.LargestWin), largestWin);
        RequireOrdered(nameof(report.LargestWin), largestWin, nameof(report.GrossProfit), grossProfit);
        RequireOrdered(nameof(report.AverageLoss), averageLoss, nameof(report.LargestLoss), largestLoss);
        RequireOrdered(nameof(report.LargestLoss), largestLoss, nameof(report.GrossLoss), grossLoss);
    }

    /// <summary>A right-censoring flag exists if and only if the duration metric it qualifies is present and Valid.</summary>
    private static void ValidateCensoringFlag(string flagName, bool? flag, string metricName, MetricValue? metric)
    {
        bool metricIsValid = metric is { Status: MetricStatus.Valid };
        if (metricIsValid != flag.HasValue)
        {
            throw Invalid($"{flagName} must be present if and only if {metricName} is present and Valid.");
        }
    }

    private static decimal? ValidValue(MetricValue? metric) => metric is { Status: MetricStatus.Valid } valid ? valid.Value : null;

    private static void RequireAtLeastZero(string name, decimal? value)
    {
        if (value < 0m) throw Invalid($"{name} must not be negative.");
    }

    private static void RequirePositive(string name, decimal? value)
    {
        if (value <= 0m) throw Invalid($"{name} must be greater than zero.");
    }

    private static void RequireWholeNonNegative(string name, decimal? value)
    {
        if (value is not { } number) return;
        if (number < 0m || number != decimal.Truncate(number)) throw Invalid($"{name} must be a non-negative whole number.");
    }

    private static void RequireCount(string name, decimal? value, int limit, string limitName)
    {
        RequireWholeNonNegative(name, value);
        if (value > limit) throw Invalid($"{name} must not exceed {limitName}.");
    }

    private static void RequireOrdered(string smallerName, decimal? smaller, string largerName, decimal? larger)
    {
        if (smaller > larger) throw Invalid($"{smallerName} must not exceed {largerName}.");
    }

    private static ArgumentException Invalid(string message) => new(message, "report");
}
