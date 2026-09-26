using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

public interface IBacktestEvaluationService
{
    BacktestEvaluationArtifact Evaluate(
        BacktestInput input,
        BacktestConfiguration configuration,
        BacktestStrategySpecification strategySpecification,
        BacktestReportOptions reportOptions,
        SamplingEvidence? samplingEvidence = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Owns the complete run-to-audit-artifact path and provides no arbitrary-result injection point.</summary>
public sealed class BacktestEvaluationService : IBacktestEvaluationService
{
    private readonly IBacktestEngine _engine;
    private readonly IQualifiedBacktestReportGenerator _reportGenerator;
    private readonly ISamplingEvidenceTrustPolicy _samplingTrustPolicy;
    private readonly ILogger<BacktestEvaluationService> _logger;

    public BacktestEvaluationService(
        IBacktestEngine engine,
        IQualifiedBacktestReportGenerator reportGenerator,
        ISamplingEvidenceTrustPolicy samplingTrustPolicy,
        ILogger<BacktestEvaluationService>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
        _samplingTrustPolicy = samplingTrustPolicy ?? throw new ArgumentNullException(nameof(samplingTrustPolicy));
        _logger = logger ?? NullLogger<BacktestEvaluationService>.Instance;
    }

    public BacktestEvaluationArtifact Evaluate(
        BacktestInput input,
        BacktestConfiguration configuration,
        BacktestStrategySpecification strategySpecification,
        BacktestReportOptions reportOptions,
        SamplingEvidence? samplingEvidence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(strategySpecification);
        ArgumentNullException.ThrowIfNull(reportOptions);
        cancellationToken.ThrowIfCancellationRequested();
        configuration.Validate();
        ValidateContext(input, reportOptions);

        IBacktestStrategy strategy = strategySpecification.CreateStrategy();
        BacktestRunFingerprintBuilder fingerprintBuilder =
            BacktestRunFingerprintBuilder.Freeze(input, configuration, strategy);
        BacktestReportOptions ownedOptions = reportOptions.WithRunFingerprintBuilder(fingerprintBuilder);
        SamplingQualification sampling = SamplingEvidenceQualifier.Qualify(
            input,
            samplingEvidence,
            _samplingTrustPolicy,
            cancellationToken);

        BacktestResult result = _engine.Run(input, configuration, strategy, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        BacktestReport? report = null;
        BacktestRunFingerprint runFingerprint;
        BootstrapDiagnostics bootstrapDiagnostics;
        DrawdownEpisodeResult ratioDrawdown;
        DrawdownEpisodeResult amountDrawdown;
        if (result.Status == RunStatus.Completed)
        {
            QualifiedBacktestReportResult generated = _reportGenerator.GenerateQualified(
                result,
                ownedOptions,
                sampling,
                cancellationToken);
            report = generated.Report;
            runFingerprint = generated.RunFingerprint;
            bootstrapDiagnostics = generated.BootstrapDiagnostics;
            (ratioDrawdown, amountDrawdown) = DrawdownEpisodeCalculator.Compute(
                result,
                ownedOptions,
                cancellationToken);
        }
        else
        {
            runFingerprint = fingerprintBuilder.Seal(result);
            bootstrapDiagnostics = BootstrapDiagnostics.NotComputed(BootstrapDiagnostics.PartialRunReason);
            ratioDrawdown = DrawdownEpisodeResult.Partial();
            amountDrawdown = DrawdownEpisodeResult.Partial();
        }

        cancellationToken.ThrowIfCancellationRequested();
        BacktestReportRunFingerprint serializableFingerprint = BacktestReportRunFingerprint.From(runFingerprint);
        BacktestEvaluationMetadata metadata = BuildMetadata(input, result, ownedOptions, report);
        EvaluationContentIdentity reportIdentity = BacktestReportIdentityEncoder.Compute(
            serializableFingerprint,
            metadata,
            result.Status,
            sampling,
            bootstrapDiagnostics,
            report,
            ratioDrawdown,
            amountDrawdown);

        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug(
            "Evaluation artifact built: Status={RunStatus}, ReportPresent={ReportPresent}, Sampling={SamplingStatus}, IdentityAvailable={IdentityAvailable}",
            result.Status,
            report is not null,
            sampling.Status,
            reportIdentity.IsAvailable);

        return new BacktestEvaluationArtifact(
            result,
            report,
            serializableFingerprint,
            metadata,
            bootstrapDiagnostics,
            sampling,
            ratioDrawdown,
            amountDrawdown,
            reportIdentity);
    }

    private static void ValidateContext(BacktestInput input, BacktestReportOptions options)
    {
        if (options.Frame != input.Frame ||
            options.HistoryStartIndex != input.HistoryStartIndex ||
            options.EvaluationStartUtc != input.EvaluationStartUtc ||
            options.EvaluationEndUtc != input.EvaluationEndUtc)
        {
            throw new ArgumentException("Report options must describe the same input frame, history boundary, and requested UTC period.", nameof(options));
        }

        // The first evaluated bar must not precede the requested start: otherwise the initial-capital time would be later than
        // the first equity label and the drawdown timeline would be reversed. An empty evaluated window has no such bar.
        if (input.HistoryStartIndex < input.Bars.Length &&
            input.Bars[input.HistoryStartIndex].Timestamp < input.EvaluationStartUtc)
        {
            throw new ArgumentException("The first evaluated bar must not be earlier than EvaluationStartUtc.", nameof(input));
        }
    }

    private static BacktestEvaluationMetadata BuildMetadata(
        BacktestInput input,
        BacktestResult result,
        BacktestReportOptions options,
        BacktestReport? report)
    {
        Assembly assembly = typeof(BacktestEvaluationService).Assembly;
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        string assemblyIdentity = $"{informationalVersion}|{assembly.ManifestModule.ModuleVersionId:D}";
        string runtimeIdentity = $"{RuntimeInformation.FrameworkDescription}|{RuntimeInformation.OSArchitecture}|{RuntimeInformation.ProcessArchitecture}";
        DateTime? lastProcessed = result.EquityPoints.IsEmpty ? null : result.EquityPoints[^1].Timestamp;
        int sampleCount = Math.Max(0, result.EquityPoints.Length - options.HistoryStartIndex);

        return new BacktestEvaluationMetadata(
            input.Symbol,
            input.DataVersion,
            input.Frame,
            input.EvaluationStartUtc,
            input.EvaluationEndUtc,
            lastProcessed,
            input.HistoryStartIndex,
            input.TradingStartIndex,
            sampleCount,
            options.AnnualPeriods,
            options.AnnualRiskFreeRate,
            options.AnnualMAR,
            options.BootstrapSeed,
            options.BootstrapIterations,
            report?.FormulaVersion ?? BacktestReport.CurrentFormulaVersion,
            assemblyIdentity,
            runtimeIdentity,
            InputDataReference.Unavailable());
    }
}
