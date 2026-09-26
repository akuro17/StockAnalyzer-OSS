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

    private static ArgumentException Invalid(string message) => new(message, "report");
}
