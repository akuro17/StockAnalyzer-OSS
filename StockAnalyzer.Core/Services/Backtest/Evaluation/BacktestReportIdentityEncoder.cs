using System.Security.Cryptography;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

internal static class BacktestReportIdentityEncoder
{
    public const int SchemaVersion = 1;
    private const string Domain = "StockAnalyzer.Backtest.Evaluation";

    public static EvaluationContentIdentity Compute(
        BacktestReportRunFingerprint runFingerprint,
        BacktestEvaluationMetadata metadata,
        RunStatus runStatus,
        SamplingQualification sampling,
        BootstrapDiagnostics bootstrap,
        BacktestReport? report,
        DrawdownEpisodeResult ratioDrawdown,
        DrawdownEpisodeResult amountDrawdown)
    {
        if (!runFingerprint.IsAvailable)
        {
            return EvaluationContentIdentity.Unavailable(SchemaVersion, EvaluationContentIdentity.RunIdentityUnavailableReason);
        }

        using var writer = new CanonicalWriter();
        writer.String(Domain);
        writer.Int32(BacktestEvaluationArtifact.CurrentAuditSchemaVersion);
        writer.Int32(SchemaVersion);

        writer.Int32(runFingerprint.SchemaVersion);
        writer.Int32(runFingerprint.ExecutionSemanticsVersion);
        writer.String(runFingerprint.Sha256);
        writer.Enum(runStatus);
        writer.Int32(BacktestEvaluationArtifact.LegacyRiskExecutionMode);

        writer.String(metadata.Symbol);
        writer.Int32(metadata.InputDataVersion);
        writer.Enum(metadata.Frame);
        writer.Timestamp(metadata.RequestedStartUtc);
        writer.Timestamp(metadata.RequestedEndUtc);
        WriteNullableTimestamp(writer, metadata.LastProcessedTimestamp);
        writer.Int32(metadata.HistoryStartIndex);
        writer.Int32(metadata.TradingStartIndex);
        writer.Int32(metadata.SampleCount);

        writer.Int32(metadata.AnnualPeriods);
        writer.Decimal(metadata.AnnualRiskFreeRate);
        writer.Decimal(metadata.AnnualMAR);
        writer.Int32(metadata.BootstrapSeed);
        writer.Int32(metadata.BootstrapIterations);
        writer.Int32(metadata.FormulaVersion);

        writer.Int32(bootstrap.MethodVersion);
        writer.NullableInt32(bootstrap.EffectiveBlockLength);
        writer.NullableInt32(bootstrap.ValidReplicates);
        writer.NullableInt32(bootstrap.ExecutedReplicates);
        writer.Bool(bootstrap.DegenerateConstant);
        writer.String(bootstrap.Reason);

        writer.String(metadata.AssemblyIdentity);
        writer.String(metadata.RuntimeIdentity);
        writer.String(metadata.InputDataReference.SourceId);
        writer.String(metadata.InputDataReference.Version);
        writer.String(metadata.InputDataReference.UnavailableReason);

        writer.Enum(sampling.Status);
        writer.Enum(sampling.Reason);
        writer.String(sampling.ProviderId);
        writer.String(sampling.CalendarId);
        writer.String(sampling.CalendarVersion);
        writer.String(sampling.ExchangeId);
        writer.String(sampling.TimeZoneId);
        writer.NullableEnum(sampling.TimestampConvention);
        writer.Enum(sampling.Frame);
        writer.NullableInt32(sampling.ExpectedCount);
        writer.Int32(sampling.ObservedCount);
        writer.String(sampling.ExpectedTimestampsDigest);

        writer.Bool(report is not null);
        if (report is not null)
        {
            WriteMetric(writer, report.TotalPnL);
            WriteMetric(writer, report.MaxDrawdownAmount);
            WriteMetric(writer, report.ExpectedPayoff);
            WriteMetric(writer, report.TotalReturn);
            WriteMetric(writer, report.CAGR);
            WriteMetric(writer, report.WinRate);
            WriteMetric(writer, report.ProfitFactor);
            WriteMetric(writer, report.MaxDrawdown);
            WriteMetric(writer, report.UlcerIndex);
            WriteMetric(writer, report.BarSharpe);
            WriteMetric(writer, report.AnnualizedSharpe);
            WriteMetric(writer, report.AnnualizedSharpeAutocorrelationAdjusted);
            WriteMetric(writer, report.BarSortino);
            WriteMetric(writer, report.AnnualizedSortino);
            WriteMetric(writer, report.AnnualizedSortinoAutocorrelationAdjusted);
            WriteMetric(writer, report.CalmarFullPeriod);
            WriteMetric(writer, report.SQN);
            WriteMetric(writer, report.RecoveryFactor);

            writer.Bool(report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval.HasValue);
            if (report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval is { } interval)
            {
                writer.Decimal(interval.Lower);
                writer.Decimal(interval.Upper);
            }
            writer.Int32(report.TotalTrades);
            writer.Int32(report.WinTrades);
            writer.Int32(report.LossTrades);
            writer.Int32(report.BreakevenTrades);
            writer.Bool(report.SqnWarning);
        }

        WriteDrawdown(writer, ratioDrawdown);
        WriteDrawdown(writer, amountDrawdown);

        string hash = Convert.ToHexString(SHA256.HashData(writer.ToArray()));
        return new EvaluationContentIdentity(true, hash, SchemaVersion, null);
    }

    private static void WriteMetric(CanonicalWriter writer, MetricValue metric)
    {
        writer.Enum(metric.Status);
        writer.Enum(metric.Unit);
        writer.Enum(metric.Reason);
        writer.NullableDecimal(metric.Value);
    }

    private static void WriteDrawdown(CanonicalWriter writer, DrawdownEpisodeResult result)
    {
        writer.Enum(result.Status);
        writer.String(result.Reason);
        writer.Bool(result.Episode is not null);
        if (result.Episode is not { } episode) return;

        writer.Int32(episode.PeakIndex);
        writer.Int32(episode.TroughIndex);
        writer.NullableInt32(episode.RecoveryIndex);
        writer.Timestamp(episode.PeakUtc);
        writer.Timestamp(episode.TroughUtc);
        WriteNullableTimestamp(writer, episode.RecoveryUtc);
        writer.Decimal(episode.PeakEquity);
        writer.Decimal(episode.TroughEquity);
        writer.Decimal(episode.Depth);
        writer.Enum(episode.Unit);
        writer.Int32(episode.DeclineBars);
        writer.NullableInt32(episode.RecoveryBars);
        writer.NullableInt32(episode.UnderwaterBars);
        // Master §3 field 10: three nullable tick values first, then the three reason strings.
        writer.NullableInt64(episode.DeclineDuration.Value?.Ticks);
        writer.NullableInt64(episode.RecoveryDuration.Value?.Ticks);
        writer.NullableInt64(episode.UnderwaterDuration.Value?.Ticks);
        writer.String(episode.DeclineDuration.Reason);
        writer.String(episode.RecoveryDuration.Reason);
        writer.String(episode.UnderwaterDuration.Reason);
    }

    private static void WriteNullableTimestamp(CanonicalWriter writer, DateTime? value)
    {
        writer.Bool(value.HasValue);
        if (value.HasValue) writer.Timestamp(value.Value);
    }
}
