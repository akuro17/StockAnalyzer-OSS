using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Evaluation;

public sealed class BacktestEvaluationServiceTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoEvidence_ProducesOwnedArtifactAndGatesQualifiedMetrics()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        BacktestEvaluationService service = Service(new ResultEngine(result), new NoSamplingEvidenceTrustPolicy());

        BacktestEvaluationArtifact artifact = service.Evaluate(
            input,
            configuration,
            BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
            Options(input, seed: 7));

        Assert.Equal(SamplingStatus.Unverified, artifact.Sampling.Status);
        Assert.Equal(SamplingReason.NoEvidence, artifact.Sampling.Reason);
        Assert.NotNull(artifact.Report);
        Assert.Equal(MetricReason.SamplingUnverified, artifact.Report!.AnnualizedSharpe.Reason);
        Assert.Equal(MetricReason.SamplingUnverified, artifact.Report.AnnualizedSharpeAutocorrelationAdjusted.Reason);
        Assert.Equal(MetricReason.SamplingUnverified, artifact.Report.AnnualizedSortino.Reason);
        Assert.Equal(MetricReason.SamplingUnverified, artifact.Report.AnnualizedSortinoAutocorrelationAdjusted.Reason);
        Assert.Equal(MetricReason.EvaluationCoverageUnverified, artifact.Report.CAGR.Reason);
        Assert.Equal(MetricReason.EvaluationCoverageUnverified, artifact.Report.CalmarFullPeriod.Reason);
        Assert.Null(artifact.Report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval);
        Assert.Equal(0, artifact.BootstrapDiagnostics.ExecutedReplicates);
        Assert.True(artifact.RunFingerprint.IsAvailable);
        Assert.True(artifact.ReportIdentity.IsAvailable);
        Assert.Equal(64, artifact.ReportIdentity.Sha256!.Length);
    }

    [Fact]
    public void ApprovedExactEvidence_IsVerifiedAndIdentityIsDeterministicAndSeedSensitive()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        var trust = new ReferenceTrustPolicy(evidence);

        BacktestEvaluationArtifact first = Service(new ResultEngine(result), trust).Evaluate(
            input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7), evidence);
        BacktestEvaluationArtifact second = Service(new ResultEngine(result), trust).Evaluate(
            input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7), evidence);
        BacktestEvaluationArtifact changedSeed = Service(new ResultEngine(result), trust).Evaluate(
            input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 8), evidence);

        Assert.Equal(SamplingStatus.Verified, first.Sampling.Status);
        Assert.Equal(SamplingReason.None, first.Sampling.Reason);
        Assert.Equal(first.ReportIdentity.Sha256, second.ReportIdentity.Sha256);
        Assert.NotEqual(first.ReportIdentity.Sha256, changedSeed.ReportIdentity.Sha256);
        Assert.NotEqual(0, first.BootstrapDiagnostics.ExecutedReplicates);
    }

    [Fact]
    public void ApprovedMalformedEvidence_IsRejectedBeforeCompletenessOrSeriesMatch()
    {
        BacktestInput input = Input(3);
        ImmutableArray<DateTime> duplicate = ImmutableArray.Create(T0, T0, T0.AddDays(2));
        SamplingEvidence evidence = Evidence(input, duplicate, complete: false);

        SamplingQualification qualification = SamplingEvidenceQualifier.Qualify(
            input,
            evidence,
            new ReferenceTrustPolicy(evidence));

        Assert.Equal(SamplingStatus.Rejected, qualification.Status);
        Assert.Equal(SamplingReason.InvalidEvidence, qualification.Reason);
        Assert.Null(qualification.ExpectedTimestampsDigest);
    }

    [Fact]
    public void UnapprovedMatchingEvidence_RemainsUnverified()
    {
        BacktestInput input = Input(3);
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());

        SamplingQualification qualification = SamplingEvidenceQualifier.Qualify(
            input,
            evidence,
            new NoSamplingEvidenceTrustPolicy());

        Assert.Equal(SamplingStatus.Unverified, qualification.Status);
        Assert.Equal(SamplingReason.UntrustedEvidence, qualification.Reason);
    }

    [Fact]
    public void UnapprovedMalformedEvidence_DoesNotPublishExpectedSeriesDigest()
    {
        BacktestInput input = Input(3);
        SamplingEvidence evidence = Evidence(input, ImmutableArray.Create(T0, T0, T0.AddDays(2)));

        SamplingQualification qualification = SamplingEvidenceQualifier.Qualify(
            input,
            evidence,
            new NoSamplingEvidenceTrustPolicy());

        Assert.Equal(SamplingStatus.Unverified, qualification.Status);
        Assert.Equal(SamplingReason.UntrustedEvidence, qualification.Reason);
        Assert.Null(qualification.ExpectedTimestampsDigest);
    }

    [Fact]
    public void ApprovedIncompleteOrMismatchedEvidence_UsesSpecifiedQualificationReasons()
    {
        BacktestInput input = Input(3);
        SamplingEvidence incomplete = Evidence(
            input,
            input.Bars.Select(bar => bar.Timestamp).ToImmutableArray(),
            complete: false);
        SamplingEvidence missing = Evidence(
            input,
            input.Bars.Take(2).Select(bar => bar.Timestamp).ToImmutableArray());

        SamplingQualification incompleteResult = SamplingEvidenceQualifier.Qualify(
            input, incomplete, new ReferenceTrustPolicy(incomplete));
        SamplingQualification missingResult = SamplingEvidenceQualifier.Qualify(
            input, missing, new ReferenceTrustPolicy(missing));

        Assert.Equal((SamplingStatus.Unverified, SamplingReason.IncompleteEvidence),
            (incompleteResult.Status, incompleteResult.Reason));
        Assert.Equal((SamplingStatus.Rejected, SamplingReason.MissingOrUnexpectedBar),
            (missingResult.Status, missingResult.Reason));
    }

    [Fact]
    public void RejectedSampling_GatesAnnualizedMetricsWithoutExecutingBootstrap()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(
            input,
            input.Bars.Take(29).Select(bar => bar.Timestamp).ToImmutableArray());

        BacktestEvaluationArtifact artifact = Evaluate(
            result,
            input,
            configuration,
            Options(input, 7),
            evidence);

        Assert.Equal(SamplingStatus.Rejected, artifact.Sampling.Status);
        Assert.Equal(MetricReason.SamplingRejected, artifact.Report!.AnnualizedSharpe.Reason);
        Assert.Equal(MetricReason.SamplingRejected, artifact.Report.AnnualizedSortinoAutocorrelationAdjusted.Reason);
        Assert.Null(artifact.Report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval);
        Assert.Equal(0, artifact.BootstrapDiagnostics.ExecutedReplicates);
    }

    [Fact]
    public void ApprovedEvidence_SupportsInitializedEmptyAndIrregularUtcSeriesWithoutInferringCadence()
    {
        BacktestInput empty = Input(0);
        SamplingEvidence emptyEvidence = Evidence(empty, ImmutableArray<DateTime>.Empty);
        SamplingQualification emptyResult = SamplingEvidenceQualifier.Qualify(
            empty, emptyEvidence, new ReferenceTrustPolicy(emptyEvidence));

        ImmutableArray<DateTime> irregular = ImmutableArray.Create(
            T0,
            T0.AddDays(4),
            T0.AddMonths(1).AddHours(1));
        BacktestInput irregularInput = Input(irregular);
        SamplingEvidence irregularEvidence = Evidence(irregularInput, irregular);
        SamplingQualification irregularResult = SamplingEvidenceQualifier.Qualify(
            irregularInput, irregularEvidence, new ReferenceTrustPolicy(irregularEvidence));

        Assert.Equal(SamplingStatus.Verified, emptyResult.Status);
        Assert.Equal(0, emptyResult.ExpectedCount);
        Assert.Equal(SamplingStatus.Verified, irregularResult.Status);
        Assert.Equal(3, irregularResult.ObservedCount);
    }

    [Fact]
    public void PartialResult_HasNoReportAndNoDrawdownEpisodes()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestResult result = Result(configuration, RunStatus.Failed, new[] { 100m, 90m, 80m });

        BacktestEvaluationArtifact artifact = Service(
            new ResultEngine(result),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input,
                configuration,
                BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
                Options(input, 7));

        Assert.Null(artifact.Report);
        Assert.Equal(DrawdownEpisodeStatus.NotComputedPartial, artifact.RatioDrawdown.Status);
        Assert.Equal(DrawdownEpisodeStatus.NotComputedPartial, artifact.AmountDrawdown.Status);
        Assert.True(artifact.ReportIdentity.IsAvailable);
    }

    [Fact]
    public void Cancellation_IsRethrownAndNoArtifactIsReturned()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        BacktestInput input = Input(3);
        BacktestConfiguration configuration = Configuration();

        Assert.ThrowsAny<OperationCanceledException>(() => Service(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 1_000m, 1_000m, 1_000m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input,
                configuration,
                BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
                Options(input, 7),
                cancellationToken: source.Token));
    }

    [Fact]
    public void DrawdownEpisode_UsesLatestEqualPeakFirstStrictTroughAndRecovery()
    {
        BacktestConfiguration configuration = Configuration(100m);
        BacktestResult result = Result(configuration, RunStatus.Completed, new[] { 120m, 120m, 90m, 90m, 120m });
        BacktestReportOptions options = new(TimeFrame.D1, 0, T0, T0.AddDays(5)) { AnnualPeriods = 252 };

        (DrawdownEpisodeResult ratio, DrawdownEpisodeResult amount) =
            DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);

        AssertEpisode(ratio, 2, 3, 5, 1, 2, 3);
        AssertEpisode(amount, 2, 3, 5, 1, 2, 3);
    }

    [Fact]
    public void DrawdownEpisode_TracksRatioAndAmountIndependently()
    {
        BacktestConfiguration configuration = Configuration(100m);
        BacktestResult result = Result(configuration, RunStatus.Completed, new[] { 50m, 200m, 120m });
        BacktestReportOptions options = new(TimeFrame.D1, 0, T0, T0.AddDays(3)) { AnnualPeriods = 252 };

        (DrawdownEpisodeResult ratio, DrawdownEpisodeResult amount) =
            DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);

        Assert.Equal(1, ratio.Episode!.TroughIndex);
        Assert.Equal(3, amount.Episode!.TroughIndex);
    }

    [Fact]
    public void DrawdownEpisode_RepresentsNoDrawdownUnrecoveredAndZeroDuration()
    {
        BacktestConfiguration configuration = Configuration(100m);
        BacktestReportOptions options = new(TimeFrame.D1, 0, T0, T0.AddDays(3)) { AnnualPeriods = 252 };

        (DrawdownEpisodeResult flatRatio, DrawdownEpisodeResult flatAmount) =
            DrawdownEpisodeCalculator.Compute(
                Result(configuration, RunStatus.Completed, new[] { 100m, 100m, 100m }),
                options,
                CancellationToken.None);
        (DrawdownEpisodeResult unrecovered, _) = DrawdownEpisodeCalculator.Compute(
            Result(configuration, RunStatus.Completed, new[] { 50m, 40m }),
            options,
            CancellationToken.None);
        (DrawdownEpisodeResult zeroDuration, _) = DrawdownEpisodeCalculator.Compute(
            Result(configuration, RunStatus.Completed, new[] { 50m }),
            options,
            CancellationToken.None);

        Assert.Equal(DrawdownEpisodeStatus.NoDrawdown, flatRatio.Status);
        Assert.Equal(DrawdownEpisodeStatus.NoDrawdown, flatAmount.Status);
        Assert.Null(unrecovered.Episode!.RecoveryIndex);
        Assert.Null(unrecovered.Episode.RecoveryBars);
        Assert.Null(unrecovered.Episode.UnderwaterBars);
        Assert.Equal(TimeSpan.Zero, zeroDuration.Episode!.DeclineDuration.Value);
    }

    [Fact]
    public void ReportIdentity_ChangesForEveryCanonicalEvaluationInputCategory()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        BacktestEvaluationArtifact baseline = Evaluate(result, input, configuration, Options(input, 7), evidence);

        BacktestReportOptions changedA = new(input.Frame, 0, input.EvaluationStartUtc, input.EvaluationEndUtc)
        {
            AnnualPeriods = 252,
            AnnualRiskFreeRate = 0.01m,
            BootstrapSeed = 7,
            BootstrapIterations = 1000,
        };
        BacktestReportOptions changedB = new(input.Frame, 0, input.EvaluationStartUtc, input.EvaluationEndUtc)
        {
            AnnualPeriods = 252,
            BootstrapSeed = 7,
            BootstrapIterations = 1001,
        };
        BacktestEvaluationArtifact a = Evaluate(result, input, configuration, changedA, evidence);
        BacktestEvaluationArtifact b = Evaluate(result, input, configuration, changedB, evidence);
        BacktestEvaluationArtifact unverified = Service(
            new ResultEngine(result),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input,
                configuration,
                BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
                Options(input, 7));

        Assert.NotEqual(baseline.ReportIdentity.Sha256, a.ReportIdentity.Sha256);
        Assert.NotEqual(baseline.ReportIdentity.Sha256, b.ReportIdentity.Sha256);
        Assert.NotEqual(baseline.ReportIdentity.Sha256, unverified.ReportIdentity.Sha256);
    }

    [Fact]
    public void StrategySpecification_OwnsParameterAndRiskCopies()
    {
        var parameter = new CoreSmaParameter { Period = 2 };
        var request = new StrategyIndicatorRequest("sma", IndicatorType.SMA, parameter);
        BacktestStrategySpecification noOp = BacktestStrategySpecification.NoOp(new[] { request });
        parameter.Period = 20;

        var risk = new BacktestRiskManagementSettings { StopLossPercent = 0.1m, TakeProfitPercent = 0.2m };
        BacktestStrategySpecification conditions = BacktestStrategySpecification.ConditionBased(
            Array.Empty<BacktestConditionEntry>(), risk);
        BacktestRiskManagementSettings exposed = conditions.RiskManagement!;

        Assert.Equal(2, Assert.IsType<CoreSmaParameter>(noOp.IndicatorRequests[0].Parameters).Period);
        Assert.NotSame(risk, exposed);
        Assert.Equal(0.1m, conditions.RiskManagement!.StopLossPercent);
        Assert.Equal(0.2m, conditions.RiskManagement.TakeProfitPercent);
    }

    [Fact]
    public async Task EvaluationExport_WritesExplicitVersionedWrapper()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestEvaluationArtifact artifact = Service(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 100m, 99m, 101m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input,
                configuration,
                BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
                Options(input, 7));
        string directory = Path.Combine(Path.GetTempPath(), $"sa-evaluation-{Guid.NewGuid():N}");
        try
        {
            await new BacktestReportExporter().ExportEvaluationAsync(artifact, directory, "evaluation.json");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "evaluation.json")));
            Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal("LegacyEvaluationEvidence", document.RootElement.GetProperty("ModelId").GetString());
            Assert.Equal(artifact.ReportIdentity.Sha256,
                document.RootElement.GetProperty("Artifact").GetProperty("ReportIdentity").GetProperty("Sha256").GetString());
            Assert.Equal((int)artifact.Sampling.Status,
                document.RootElement.GetProperty("Artifact").GetProperty("Sampling").GetProperty("Status").GetInt32());
            Assert.Equal((int)artifact.RatioDrawdown.Status,
                document.RootElement.GetProperty("Artifact").GetProperty("RatioDrawdown").GetProperty("Status").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyGenerate_RemainsUngated()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        BacktestReport report = new BacktestReportGenerator().Generate(result, Options(input, 7));

        Assert.NotEqual(MetricReason.SamplingUnverified, report.AnnualizedSharpe.Reason);
        Assert.NotEqual(MetricReason.EvaluationCoverageUnverified, report.CAGR.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // F01: independently authored canonical preimage (little-endian fields written per master §3;
    // never calls CanonicalWriter or BacktestReportIdentityEncoder).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ReportIdentity_MatchesIndependentlyAuthoredPreimage_IncludingDurationOrder()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        BacktestEvaluationArtifact artifact = Evaluate(result, input, configuration, Options(input, 7), evidence);

        Assert.Equal(DrawdownEpisodeStatus.Available, artifact.RatioDrawdown.Status);
        Assert.NotNull(artifact.RatioDrawdown.Episode!.RecoveryIndex);
        Assert.Equal(ExpectedIdentityHex(artifact), artifact.ReportIdentity.Sha256);
    }

    [Fact]
    public void ReportIdentity_DurationBlockIsThreeTicksThenThreeReasons()
    {
        // A recovered episode and an unrecovered episode exercise present and absent durations.
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestEvaluationArtifact recovered = Service(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 900m, 1_000m, 1_000m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7));
        BacktestEvaluationArtifact unrecovered = Service(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 900m, 800m, 700m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7));

        Assert.NotNull(recovered.AmountDrawdown.Episode!.RecoveryUtc);
        Assert.Null(unrecovered.AmountDrawdown.Episode!.RecoveryUtc);
        Assert.Equal(ExpectedIdentityHex(recovered), recovered.ReportIdentity.Sha256);
        Assert.Equal(ExpectedIdentityHex(unrecovered), unrecovered.ReportIdentity.Sha256);
    }

    [Fact]
    public void ReportIdentity_ChangesForEachIndependentlyChangedInput()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        string baseline = Evaluate(result, input, configuration, Options(input, 7), evidence).ReportIdentity.Sha256!;

        BacktestReportOptions With(int periods = 252, decimal rf = 0m, decimal mar = 0m, int seed = 7, int b = 1000) =>
            new(input.Frame, 0, input.EvaluationStartUtc, input.EvaluationEndUtc)
            {
                AnnualPeriods = periods,
                AnnualRiskFreeRate = rf,
                AnnualMAR = mar,
                BootstrapSeed = seed,
                BootstrapIterations = b,
            };

        var changed = new Dictionary<string, string>
        {
            ["A"] = Evaluate(result, input, configuration, With(periods: 251), evidence).ReportIdentity.Sha256!,
            ["rf"] = Evaluate(result, input, configuration, With(rf: 0.01m), evidence).ReportIdentity.Sha256!,
            ["mar"] = Evaluate(result, input, configuration, With(mar: 0.01m), evidence).ReportIdentity.Sha256!,
            ["seed"] = Evaluate(result, input, configuration, With(seed: 8), evidence).ReportIdentity.Sha256!,
            ["B"] = Evaluate(result, input, configuration, With(b: 1001), evidence).ReportIdentity.Sha256!,
        };
        foreach ((string name, string hash) in changed)
        {
            Assert.True(baseline != hash, $"Changing {name} must change the report identity.");
        }
        Assert.Equal(changed.Count, changed.Values.Distinct().Count());
    }

    [Fact]
    public void ReportIdentity_DistinguishesDecimalScale()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestResult result = Result(configuration, RunStatus.Completed, new[] { 1_000m, 1_010m, 1_020m });
        BacktestReportOptions One(decimal rf) => new(input.Frame, 0, input.EvaluationStartUtc, input.EvaluationEndUtc)
        {
            AnnualPeriods = 252,
            AnnualRiskFreeRate = rf,
        };
        BacktestEvaluationService service = Service(new ResultEngine(result), new NoSamplingEvidenceTrustPolicy());
        BacktestStrategySpecification noOp = BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>());

        string scale1 = service.Evaluate(input, configuration, noOp, One(1.0m)).ReportIdentity.Sha256!;
        string scale2 = service.Evaluate(input, configuration, noOp, One(1.00m)).ReportIdentity.Sha256!;

        Assert.NotEqual(scale1, scale2);
    }

    // ---------------------------------------------------------------------------------------------
    // F02: qualified CAGR precedence
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 100, 200, false, MetricStatus.Undefined, MetricReason.ZeroPeriod)]          // Start == End beats coverage
    [InlineData(2, 100, 200, false, MetricStatus.NotApplicable, MetricReason.EvaluationCoverageUnverified)] // 1-tick period would overflow Pow; gate first
    [InlineData(2, 100, -1, false, MetricStatus.Undefined, MetricReason.NegativeFinalEquity)]  // domain guard before coverage
    [InlineData(2, 100, 0, false, MetricStatus.NotApplicable, MetricReason.EvaluationCoverageUnverified)] // unverified Em==0 is N/A, not -1
    public void QualifiedCagr_AppliesDomainGuardsThenCoverageGate(int tickSpan, int start, int end, bool verified, MetricStatus status, MetricReason reason)
    {
        var options = new BacktestReportOptions(TimeFrame.D1, 0, T0, T0.AddTicks(tickSpan)) { AnnualPeriods = 252 };
        MetricValue cagr = BasicMetricsCalculator.ComputeQualifiedCagr(
            ImmutableArray.Create((decimal)start, (decimal)end), options, verified);

        Assert.Equal(status, cagr.Status);
        Assert.Equal(reason, cagr.Reason);
        Assert.Null(cagr.Value);
    }

    [Fact]
    public void QualifiedCagr_VerifiedZeroFinalEquityIsMinusOneAndCalmarPropagatesCoverage()
    {
        var options = new BacktestReportOptions(TimeFrame.D1, 0, T0, T0.AddDays(365)) { AnnualPeriods = 252 };
        MetricValue verified = BasicMetricsCalculator.ComputeQualifiedCagr(ImmutableArray.Create(100m, 0m), options, true);
        MetricValue unverified = BasicMetricsCalculator.ComputeQualifiedCagr(ImmutableArray.Create(100m, 0m), options, false);
        MetricValue legacy = BasicMetricsCalculator.ComputeCagr(ImmutableArray.Create(100m, 0m), options);
        MetricValue calmar = RiskAdjustedMetricsCalculator.ComputeCalmarFullPeriod(
            unverified, MetricValue.Valid(0.5m, MetricUnit.DrawdownRatio));

        Assert.Equal(-1m, verified.Value);
        Assert.Equal(-1m, legacy.Value);
        Assert.Equal(MetricReason.EvaluationCoverageUnverified, unverified.Reason);
        Assert.Equal(MetricStatus.NotApplicable, calmar.Status);
        Assert.Equal(MetricReason.EvaluationCoverageUnverified, calmar.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // F04 / F05: drawdown episodes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DrawdownEpisode_RejectsNonUtcTimelineEvenWhenEquityIsFlat()
    {
        BacktestConfiguration configuration = Configuration(100m);
        DateTime local = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Local);
        BacktestResult result = ResultAt(configuration, new[] { 100m, 100m }, new[] { local, local.AddDays(1) });
        BacktestReportOptions options = new(TimeFrame.D1, 0, T0, T0.AddDays(2)) { AnnualPeriods = 252 };

        (DrawdownEpisodeResult ratio, DrawdownEpisodeResult amount) = DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);

        AssertUnavailable(ratio, "InvalidInput");
        AssertUnavailable(amount, "InvalidInput");
    }

    [Fact]
    public void DrawdownEpisode_RejectsDescendingAndDuplicateObservedBars()
    {
        BacktestConfiguration configuration = Configuration(100m);
        BacktestReportOptions options = new(TimeFrame.D1, 0, T0, T0.AddDays(3)) { AnnualPeriods = 252 };
        DateTime day1 = T0.AddDays(1);

        (DrawdownEpisodeResult descRatio, DrawdownEpisodeResult descAmount) = DrawdownEpisodeCalculator.Compute(
            ResultAt(configuration, new[] { 90m, 80m }, new[] { day1.AddDays(1), day1 }), options, CancellationToken.None);
        (DrawdownEpisodeResult flatDescending, _) = DrawdownEpisodeCalculator.Compute(
            ResultAt(configuration, new[] { 100m, 100m }, new[] { day1.AddDays(1), day1 }), options, CancellationToken.None);
        (DrawdownEpisodeResult duplicate, _) = DrawdownEpisodeCalculator.Compute(
            ResultAt(configuration, new[] { 90m, 80m }, new[] { day1, day1 }), options, CancellationToken.None);
        (DrawdownEpisodeResult beforeStart, _) = DrawdownEpisodeCalculator.Compute(
            ResultAt(configuration, new[] { 90m }, new[] { T0.AddDays(-1) }), options, CancellationToken.None);

        AssertUnavailable(descRatio, "InvalidInput");
        AssertUnavailable(descAmount, "InvalidInput");
        AssertUnavailable(flatDescending, "InvalidInput");
        AssertUnavailable(duplicate, "InvalidInput");
        AssertUnavailable(beforeStart, "InvalidInput");
    }

    [Fact]
    public void DrawdownEpisode_AllowsInitialTimeEqualToFirstBar()
    {
        BacktestConfiguration configuration = Configuration(100m);
        BacktestResult result = ResultAt(configuration, new[] { 90m, 100m }, new[] { T0, T0.AddDays(1) });
        BacktestReportOptions options = new(TimeFrame.D1, 0, T0, T0.AddDays(2)) { AnnualPeriods = 252 };

        (DrawdownEpisodeResult ratio, _) = DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);

        Assert.Equal(DrawdownEpisodeStatus.Available, ratio.Status);
        Assert.Equal(TimeSpan.Zero, ratio.Episode!.DeclineDuration.Value);
        Assert.Equal(0, ratio.Episode.PeakIndex);
        Assert.Equal(1, ratio.Episode.TroughIndex);
    }

    [Fact]
    public void DrawdownEpisode_RatioOverflowDoesNotHideValidAmountEpisode()
    {
        BacktestConfiguration configuration = Configuration(0.0000000000000000000000000001m);
        BacktestResult result = Result(configuration, RunStatus.Completed, new[] { -10m });
        BacktestReportOptions options = new(TimeFrame.D1, 0, T0, T0.AddDays(1)) { AnnualPeriods = 252 };

        (DrawdownEpisodeResult ratio, DrawdownEpisodeResult amount) = DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);

        Assert.Equal(DrawdownEpisodeStatus.Unavailable, ratio.Status);
        Assert.Equal(DrawdownEpisodeStatus.Available, amount.Status);
        Assert.Equal(0.0000000000000000000000000001m + 10m, amount.Episode!.Depth);
        Assert.Equal(MetricUnit.Currency, amount.Episode.Unit);
    }

    [Fact]
    public void DrawdownEpisode_KeepsFirstTroughOnEqualDepthAndMatchesScalarMaxima()
    {
        BacktestConfiguration configuration = Configuration(100m);
        BacktestInput input = Input(3);
        BacktestResult result = Result(configuration, RunStatus.Completed, new[] { 90m, 90m, 95m });
        BacktestReportOptions options = Options(input, 7);

        (DrawdownEpisodeResult ratio, DrawdownEpisodeResult amount) = DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);
        BacktestReport report = new BacktestReportGenerator().Generate(result, options);

        Assert.Equal(1, ratio.Episode!.TroughIndex);
        Assert.Equal(1, amount.Episode!.TroughIndex);
        Assert.Equal(report.MaxDrawdown.Value, ratio.Episode.Depth);
        Assert.Equal(report.MaxDrawdownAmount.Value, amount.Episode.Depth);
        Assert.Equal(MetricUnit.DrawdownRatio, ratio.Episode.Unit);
    }

    // ---------------------------------------------------------------------------------------------
    // F03: ownership
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BacktestResult_OwnsReproducibilityHashOnInputAndOutput()
    {
        byte[] source = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        BacktestResult result = new(
            ImmutableArray<BacktestOrder>.Empty, ImmutableArray<BacktestFill>.Empty, ImmutableArray<BacktestTrade>.Empty,
            ImmutableArray<EquityPoint>.Empty, ImmutableArray<BacktestSignal>.Empty, Configuration(), RunStatus.Completed, "NoOp", source, false);

        source[0] = 0xFF;
        byte[] first = result.ReproducibilityHash;
        first[1] = 0xFF;

        Assert.Equal(0, result.ReproducibilityHash[0]);
        Assert.Equal(1, result.ReproducibilityHash[1]);
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), result.ReproducibilityHash);
    }

    [Fact]
    public async Task ArtifactResult_HashMutationNeverChangesExportOrIdentity()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestEvaluationArtifact artifact = Service(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 100m, 99m, 101m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7));
        string directory = Path.Combine(Path.GetTempPath(), $"sa-evaluation-{Guid.NewGuid():N}");
        try
        {
            await new BacktestReportExporter().ExportEvaluationAsync(artifact, directory, "before.json");
            artifact.Result.ReproducibilityHash[0] ^= 0xFF;
            await new BacktestReportExporter().ExportEvaluationAsync(artifact, directory, "after.json");

            Assert.Equal(
                await File.ReadAllTextAsync(Path.Combine(directory, "before.json")),
                await File.ReadAllTextAsync(Path.Combine(directory, "after.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StrategySpecification_GetterCopiesDoNotAliasStoredParameters()
    {
        var request = new StrategyIndicatorRequest("sma", IndicatorType.SMA, new CoreSmaParameter { Period = 2 });
        BacktestStrategySpecification noOp = BacktestStrategySpecification.NoOp(new[] { request });

        Assert.IsType<CoreSmaParameter>(noOp.IndicatorRequests[0].Parameters).Period = 99;

        Assert.Equal(2, Assert.IsType<CoreSmaParameter>(noOp.IndicatorRequests[0].Parameters).Period);
    }

    // ---------------------------------------------------------------------------------------------
    // F06: typed archive roundtrip
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExportedEvidence_RoundTripsThroughTypedArchive()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        BacktestReportOptions options = new(input.Frame, 0, input.EvaluationStartUtc, input.EvaluationEndUtc)
        {
            AnnualPeriods = 252,
            AnnualRiskFreeRate = 0.0100m,
            AnnualMAR = 0.0050m,
            BootstrapSeed = 7,
            BootstrapIterations = 1000,
        };
        BacktestEvaluationArtifact artifact = Evaluate(result, input, configuration, options, evidence);
        string directory = Path.Combine(Path.GetTempPath(), $"sa-evaluation-{Guid.NewGuid():N}");
        try
        {
            await new BacktestReportExporter().ExportEvaluationAsync(artifact, directory, "evaluation.json");
            BacktestEvaluationArchive archive = BacktestEvaluationArchiveReader.Read(
                await File.ReadAllTextAsync(Path.Combine(directory, "evaluation.json")));

            Assert.Equal(BacktestEvaluationExportEnvelope.CurrentSchemaVersion, archive.SchemaVersion);
            Assert.Equal(BacktestEvaluationExportEnvelope.LegacyModelId, archive.ModelId);
            ArchivedArtifact restored = archive.Artifact!;
            Assert.Equal(artifact.RunStatus, restored.RunStatus);
            Assert.Equal(artifact.RiskExecutionMode, restored.RiskExecutionMode);

            Assert.Equal(artifact.ReportIdentity.Sha256, restored.ReportIdentity!.Sha256);
            Assert.Equal(artifact.ReportIdentity.SchemaVersion, restored.ReportIdentity.SchemaVersion);
            Assert.Equal(artifact.RunFingerprint, restored.RunFingerprint);

            BacktestEvaluationMetadata m = artifact.Metadata;
            ArchivedMetadata rm = restored.Metadata!;
            Assert.Equal(m.Symbol, rm.Symbol);
            Assert.Equal(m.Frame, rm.Frame);
            AssertSameUtc(m.RequestedStartUtc, rm.RequestedStartUtc);
            AssertSameUtc(m.RequestedEndUtc, rm.RequestedEndUtc);
            AssertSameUtc(m.LastProcessedTimestamp!.Value, rm.LastProcessedTimestamp!.Value);
            Assert.Equal((m.HistoryStartIndex, m.TradingStartIndex, m.SampleCount), (rm.HistoryStartIndex, rm.TradingStartIndex, rm.SampleCount));
            Assert.Equal((m.AnnualPeriods, m.BootstrapSeed, m.BootstrapIterations, m.FormulaVersion), (rm.AnnualPeriods, rm.BootstrapSeed, rm.BootstrapIterations, rm.FormulaVersion));
            AssertSameDecimal(m.AnnualRiskFreeRate, rm.AnnualRiskFreeRate);
            AssertSameDecimal(m.AnnualMAR, rm.AnnualMAR);
            Assert.Equal((m.AssemblyIdentity, m.RuntimeIdentity), (rm.AssemblyIdentity, rm.RuntimeIdentity));
            Assert.Equal(m.InputDataReference.UnavailableReason, rm.InputDataReference!.UnavailableReason);

            BootstrapDiagnostics b = artifact.BootstrapDiagnostics;
            ArchivedBootstrap rb = restored.BootstrapDiagnostics!;
            Assert.Equal((b.MethodVersion, b.EffectiveBlockLength, b.ValidReplicates, b.ExecutedReplicates, b.DegenerateConstant, b.Reason),
                (rb.MethodVersion, rb.EffectiveBlockLength, rb.ValidReplicates, rb.ExecutedReplicates, rb.DegenerateConstant, rb.Reason));

            SamplingQualification s = artifact.Sampling;
            ArchivedSampling rs = restored.Sampling!;
            Assert.Equal((s.Status, s.Reason, s.ProviderId, s.CalendarId, s.CalendarVersion, s.ExchangeId, s.TimeZoneId),
                (rs.Status, rs.Reason, rs.ProviderId, rs.CalendarId, rs.CalendarVersion, rs.ExchangeId, rs.TimeZoneId));
            Assert.Equal((s.TimestampConvention, s.Frame, s.ExpectedCount, s.ObservedCount, s.ExpectedTimestampsDigest),
                (rs.TimestampConvention, rs.Frame, rs.ExpectedCount, rs.ObservedCount, rs.ExpectedTimestampsDigest));

            AssertSameDrawdown(artifact.RatioDrawdown, restored.RatioDrawdown!);
            AssertSameDrawdown(artifact.AmountDrawdown, restored.AmountDrawdown!);

            BacktestReport report = artifact.Report!;
            BacktestReport restoredReport = restored.Report!;
            Assert.Equal(report.CAGR, restoredReport.CAGR);
            Assert.Equal(report.AnnualizedSortinoAutocorrelationAdjusted, restoredReport.AnnualizedSortinoAutocorrelationAdjusted);
            Assert.Equal(report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval, restoredReport.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval);
            Assert.Equal(report.RunFingerprint, restoredReport.RunFingerprint);
            Assert.Equal(report.TotalTrades, restoredReport.TotalTrades);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TypedArchiveReader_RejectsUnsupportedOrIncompleteDocuments()
    {
        Assert.Throws<InvalidDataException>(() => BacktestEvaluationArchiveReader.Read("{\"SchemaVersion\":1,\"ModelId\":\"LegacyEvaluationEvidence\"}"));
        Assert.Throws<InvalidDataException>(() => BacktestEvaluationArchiveReader.Read("{\"SchemaVersion\":2,\"ModelId\":\"LegacyEvaluationEvidence\",\"Artifact\":{}}"));
        Assert.Throws<InvalidDataException>(() => BacktestEvaluationArchiveReader.Read("not json"));
    }

    // ---------------------------------------------------------------------------------------------
    // F08 / A1 / A7: service ownership, logger, cancellation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Service_RunsEngineOnceAndGeneratesReportOnlyForCompletedRuns()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        foreach ((RunStatus status, int expectedReports) in new[] { (RunStatus.Completed, 1), (RunStatus.Failed, 0), (RunStatus.Cancelled, 0) })
        {
            var engine = new CountingEngine(Result(configuration, status, new[] { 100m, 99m, 101m }));
            var generator = new CountingGenerator();
            var service = new BacktestEvaluationService(engine, generator, new NoSamplingEvidenceTrustPolicy());

            BacktestEvaluationArtifact artifact = service.Evaluate(
                input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7));

            Assert.Equal(1, engine.Runs);
            Assert.Equal(expectedReports, generator.Calls);
            Assert.Equal(expectedReports == 1, artifact.Report is not null);
            Assert.True(artifact.RunFingerprint.IsAvailable);
        }
    }

    [Fact]
    public void Service_AcceptsOptionalLoggerAndRejectsNullRequiredArguments()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        var service = new BacktestEvaluationService(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 100m, 99m, 101m })),
            new BacktestReportGenerator(),
            new NoSamplingEvidenceTrustPolicy(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BacktestEvaluationService>.Instance);
        BacktestStrategySpecification noOp = BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>());

        Assert.NotNull(service.Evaluate(input, configuration, noOp, Options(input, 7)));
        Assert.Throws<ArgumentNullException>(() => service.Evaluate(null!, configuration, noOp, Options(input, 7)));
        Assert.Throws<ArgumentNullException>(() => service.Evaluate(input, null!, noOp, Options(input, 7)));
        Assert.Throws<ArgumentNullException>(() => service.Evaluate(input, configuration, null!, Options(input, 7)));
        Assert.Throws<ArgumentNullException>(() => service.Evaluate(input, configuration, noOp, null!));
    }

    [Fact]
    public void SamplingQualification_ObservesCancellationInsideLargeSeries()
    {
        BacktestInput input = Input(3_000);
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SamplingEvidenceQualifier.Qualify(
            input, evidence, new ReferenceTrustPolicy(evidence), source.Token));
    }

    [Fact]
    public void PartialRuns_ReportUnavailableIdentityOnlyWhenFingerprintIsUnavailable()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestEvaluationArtifact artifact = Service(
            new ResultEngine(Result(configuration, RunStatus.Cancelled, new[] { 100m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7));

        Assert.Null(artifact.Report);
        Assert.Equal("NotComputedPartial", artifact.BootstrapDiagnostics.Reason);
        Assert.Equal(0, artifact.BootstrapDiagnostics.ExecutedReplicates);
        Assert.True(artifact.ReportIdentity.IsAvailable);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers for the tests above
    // ---------------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------------
    // M1: archive reader invariants (each mutation of a valid export must be rejected)
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string> InvalidArchiveMutations => new()
    {
        "ratio-partial-status-on-completed-run",
        "ratio-available-without-episode",
        "ratio-no-drawdown-with-episode",
        "report-identity-short-digest",
        "report-identity-lower-case-digest",
        "run-fingerprint-unavailable-with-digest",
        "report-identity-available-when-run-unavailable",
        "episode-decline-bars-mismatch",
        "episode-unit-mismatch",
        "episode-missing-duration",
        "episode-recovery-not-after-trough",
        "episode-negative-depth",
        "episode-duration-value-and-reason",
        "metadata-blank-symbol",
        "metadata-reversed-period",
        "metadata-non-utc-start",
        "metadata-trading-before-history",
        "sampling-verified-with-reason",
        "sampling-bad-digest",
        "bootstrap-negative-executed",
        "completed-without-report",
        "report-fingerprint-differs",
    };

    [Theory]
    [MemberData(nameof(InvalidArchiveMutations))]
    public async Task TypedArchiveReader_RejectsInconsistentDocument(string mutation)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(await ExportValidJsonAsync())!;
        System.Text.Json.Nodes.JsonNode artifact = root["Artifact"]!;
        System.Text.Json.Nodes.JsonNode ratio = artifact["RatioDrawdown"]!;
        System.Text.Json.Nodes.JsonNode episode = ratio["Episode"]!;
        Assert.NotNull(episode["RecoveryIndex"]);

        switch (mutation)
        {
            case "ratio-partial-status-on-completed-run": ratio["Status"] = (int)DrawdownEpisodeStatus.NotComputedPartial; ratio["Reason"] = "x"; ratio["Episode"] = null; break;
            case "ratio-available-without-episode": ratio["Episode"] = null; break;
            case "ratio-no-drawdown-with-episode": ratio["Status"] = (int)DrawdownEpisodeStatus.NoDrawdown; ratio["Reason"] = DrawdownReasonCodes.NoDrawdown; break;
            case "report-identity-short-digest": artifact["ReportIdentity"]!["Sha256"] = "ABC"; break;
            case "report-identity-lower-case-digest": artifact["ReportIdentity"]!["Sha256"] = artifact["ReportIdentity"]!["Sha256"]!.GetValue<string>().ToLowerInvariant(); break;
            case "run-fingerprint-unavailable-with-digest": artifact["RunFingerprint"]!["IsAvailable"] = false; artifact["RunFingerprint"]!["UnavailableReason"] = "x"; break;
            case "report-identity-available-when-run-unavailable":
                artifact["RunFingerprint"]!["IsAvailable"] = false;
                artifact["RunFingerprint"]!["Sha256"] = null;
                artifact["RunFingerprint"]!["UnavailableReason"] = "x";
                artifact["Report"]!["RunFingerprint"] = artifact["RunFingerprint"]!.DeepClone();
                break;
            case "episode-decline-bars-mismatch": episode["DeclineBars"] = episode["DeclineBars"]!.GetValue<int>() + 1; break;
            case "episode-unit-mismatch": episode["Unit"] = (int)MetricUnit.Currency; break;
            case "episode-missing-duration": episode["DeclineDuration"] = null; break;
            case "episode-recovery-not-after-trough": episode["RecoveryIndex"] = episode["TroughIndex"]!.GetValue<int>(); break;
            case "episode-negative-depth": episode["Depth"] = -0.1m; break;
            case "episode-duration-value-and-reason": episode["DeclineDuration"]!["Reason"] = "x"; break;
            case "metadata-blank-symbol": artifact["Metadata"]!["Symbol"] = " "; break;
            case "metadata-reversed-period": artifact["Metadata"]!["RequestedStartUtc"] = "2100-01-01T00:00:00Z"; break;
            case "metadata-non-utc-start": artifact["Metadata"]!["RequestedStartUtc"] = "2024-01-01T00:00:00"; break;
            case "metadata-trading-before-history": artifact["Metadata"]!["HistoryStartIndex"] = 5; artifact["Metadata"]!["TradingStartIndex"] = 1; break;
            case "sampling-verified-with-reason": artifact["Sampling"]!["Reason"] = (int)SamplingReason.NoEvidence; break;
            case "sampling-bad-digest": artifact["Sampling"]!["ExpectedTimestampsDigest"] = "zz"; break;
            case "bootstrap-negative-executed": artifact["BootstrapDiagnostics"]!["ExecutedReplicates"] = -1; break;
            case "completed-without-report": artifact["Report"] = null; break;
            case "report-fingerprint-differs": artifact["Report"]!["RunFingerprint"]!["ExecutionSemanticsVersion"] = 99; break;
            default: throw new InvalidOperationException(mutation);
        }

        // An invariant violation carries no inner exception; a JSON/type failure would, so this proves the intended rule fired.
        InvalidDataException rejection = Assert.Throws<InvalidDataException>(() => BacktestEvaluationArchiveReader.Read(root.ToJsonString()));
        Assert.Null(rejection.InnerException);
    }

    [Fact]
    public async Task TypedArchiveReader_AcceptsUnmodifiedExportAndPartialRunExport()
    {
        Assert.NotNull(BacktestEvaluationArchiveReader.Read(await ExportValidJsonAsync()).Artifact);

        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestEvaluationArtifact partial = Service(
            new ResultEngine(Result(configuration, RunStatus.Cancelled, new[] { 100m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7));

        ArchivedArtifact restored = BacktestEvaluationArchiveReader.Read(await ExportJsonAsync(partial)).Artifact!;

        Assert.Null(restored.Report);
        Assert.Equal(DrawdownEpisodeStatus.NotComputedPartial, restored.RatioDrawdown!.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // M3: additional acceptance matrix
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ReportIdentity_ChangesForRequestedPeriodAndSamplingMetadata()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        string baseline = Evaluate(result, input, configuration, Options(input, 7), evidence).ReportIdentity.Sha256!;

        BacktestInput longerPeriod = new(input.Bars, input.Symbol, input.Frame, BacktestInput.CurrentDataVersion,
            input.EvaluationStartUtc, input.EvaluationEndUtc.AddDays(1), 0, 0);
        SamplingEvidence otherCalendar = new(
            "fixture-provider", "fixture-calendar", "v2", "XTEST", "UTC", input.Symbol, input.Frame, TimestampConvention.PeriodEnd,
            input.EvaluationStartUtc, input.EvaluationEndUtc, true, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());

        string period = Evaluate(result, longerPeriod, configuration, Options(longerPeriod, 7),
            Evidence(longerPeriod, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray())).ReportIdentity.Sha256!;
        string calendar = Evaluate(result, input, configuration, Options(input, 7), otherCalendar).ReportIdentity.Sha256!;

        Assert.NotEqual(baseline, period);
        Assert.NotEqual(baseline, calendar);
        Assert.NotEqual(period, calendar);
    }

    [Fact]
    public void ReportIdentity_UnavailableRunIdentityGivesUnavailableReportIdentityButKeepsMetadata()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestEvaluationArtifact artifact = Service(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 100m, 99m, 101m })),
            new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()), Options(input, 7));
        var unavailable = new BacktestReportRunFingerprint { IsAvailable = false, UnavailableReason = "fixture" };

        EvaluationContentIdentity identity = BacktestReportIdentityEncoder.Compute(
            unavailable, artifact.Metadata, artifact.RunStatus, artifact.Sampling, artifact.BootstrapDiagnostics,
            artifact.Report, artifact.RatioDrawdown, artifact.AmountDrawdown);

        Assert.False(identity.IsAvailable);
        Assert.Null(identity.Sha256);
        Assert.Equal(EvaluationContentIdentity.RunIdentityUnavailableReason, identity.UnavailableReason);
        Assert.Equal("TEST", artifact.Metadata.Symbol);
    }

    [Fact]
    public void ServiceBoundary_SingletonAndStartEqualsEndInputsAreEvaluatedWithoutFabricatedBars()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(1);
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());

        BacktestEvaluationArtifact artifact = Evaluate(
            Result(configuration, RunStatus.Completed, new[] { 1_010m }), input, configuration, Options(input, 7), evidence);

        Assert.Equal(SamplingStatus.Verified, artifact.Sampling.Status);
        Assert.Equal(1, artifact.Sampling.ObservedCount);
        Assert.Equal(MetricStatus.Undefined, artifact.Report!.CAGR.Status);
        Assert.Equal(MetricReason.ZeroPeriod, artifact.Report.CAGR.Reason);
        Assert.Equal(MetricReason.SampleTooSmall, artifact.Report.AnnualizedSortinoAutocorrelationAdjusted.Reason);
    }

    [Fact]
    public void ApprovedButInvalidEvidence_IsRejectedForEveryStructuralDefect()
    {
        BacktestInput input = Input(3);
        ImmutableArray<DateTime> good = input.Bars.Select(bar => bar.Timestamp).ToImmutableArray();
        DateTime local = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
        SamplingEvidence With(
            string provider = "p", string symbol = "TEST", TimeFrame frame = TimeFrame.D1,
            DateTime? start = null, ImmutableArray<DateTime>? stamps = null, TimestampConvention convention = TimestampConvention.PeriodEnd) => new(
                provider, "cal", "v1", "X", "UTC", symbol, frame, convention,
                start ?? input.EvaluationStartUtc, input.EvaluationEndUtc, true, stamps ?? good);

        SamplingEvidence[] defects =
        {
            With(provider: " "),
            With(symbol: "OTHER"),
            With(frame: (TimeFrame)999),
            With(convention: (TimestampConvention)7),
            With(start: T0.AddDays(-1)),
            With(stamps: default(ImmutableArray<DateTime>)),
            With(stamps: ImmutableArray.Create(local, T0.AddDays(1), T0.AddDays(2))),
            With(stamps: ImmutableArray.Create(T0.AddDays(2), T0.AddDays(1), T0)),
            With(stamps: ImmutableArray.Create(T0, T0.AddDays(1), T0.AddDays(3))),
        };

        foreach (SamplingEvidence evidence in defects)
        {
            SamplingQualification qualification = SamplingEvidenceQualifier.Qualify(input, evidence, new ReferenceTrustPolicy(evidence));
            Assert.Equal((SamplingStatus.Rejected, SamplingReason.InvalidEvidence), (qualification.Status, qualification.Reason));
            Assert.Null(qualification.ExpectedTimestampsDigest);
        }
    }

    [Fact]
    public void Cancellation_AfterEngineOrDuringReportGeneration_NeverReturnsAnArtifact()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(3);
        BacktestStrategySpecification noOp = BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>());
        BacktestResult result = Result(configuration, RunStatus.Completed, new[] { 100m, 99m, 101m });

        using var afterEngine = new CancellationTokenSource();
        var cancellingEngine = new CancellingEngine(result, afterEngine);
        Assert.ThrowsAny<OperationCanceledException>(() => new BacktestEvaluationService(
            cancellingEngine, new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, noOp, Options(input, 7), cancellationToken: afterEngine.Token));

        using var duringReport = new CancellationTokenSource();
        var cancellingGenerator = new CancellingGenerator(duringReport);
        Assert.ThrowsAny<OperationCanceledException>(() => new BacktestEvaluationService(
            new ResultEngine(result), cancellingGenerator, new NoSamplingEvidenceTrustPolicy()).Evaluate(
                input, configuration, noOp, Options(input, 7), cancellationToken: duringReport.Token));
        Assert.Equal(1, cancellingGenerator.Calls);
    }

    [Fact]
    public void ReasonCodesAndEnumMembers_HaveLocalizationKeysInBothLocales()
    {
        string root = FindRepositoryRoot();
        var locales = new[] { "en", "ja" }
            .Select(name => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
                Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", name + ".json")))!.AsObject())
            .ToArray();

        var keys = new List<string>();
        keys.AddRange(Enum.GetNames<SamplingStatus>().Select(n => "Backtest_Audit_SamplingStatus_" + n));
        keys.AddRange(Enum.GetNames<SamplingReason>().Select(n => "Backtest_Audit_SamplingReason_" + n));
        keys.AddRange(Enum.GetNames<DrawdownEpisodeStatus>().Select(n => "Backtest_Audit_DrawdownStatus_" + n));
        keys.AddRange(Enum.GetNames<MetricUnit>().Select(n => "Backtest_Audit_Unit_" + n));
        keys.AddRange(typeof(DrawdownReasonCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => "Backtest_Audit_Reason_" + (string)f.GetRawConstantValue()!));
        keys.Add("Backtest_Audit_Reason_" + EvaluationContentIdentity.RunIdentityUnavailableReason);
        keys.Add("Backtest_Audit_Reason_" + InputDataReference.ProviderUnavailableReason);
        keys.Add("Backtest_Audit_RiskMode_" + BacktestEvaluationArtifact.LegacyRiskExecutionMode);

        foreach (var locale in locales)
        {
            string[] missing = keys.Where(key => !locale.ContainsKey(key)).ToArray();
            Assert.True(missing.Length == 0, "Missing localization keys: " + string.Join(", ", missing));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root (StockAnalyzer.sln) not found.");
    }

    private static Task<string> ExportValidJsonAsync()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestInput input = Input(30);
        BacktestResult result = Result(configuration, RunStatus.Completed, Equity(30));
        SamplingEvidence evidence = Evidence(input, input.Bars.Select(bar => bar.Timestamp).ToImmutableArray());
        return ExportJsonAsync(Evaluate(result, input, configuration, Options(input, 7), evidence));
    }

    private static async Task<string> ExportJsonAsync(BacktestEvaluationArtifact artifact)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sa-evaluation-{Guid.NewGuid():N}");
        try
        {
            await new BacktestReportExporter().ExportEvaluationAsync(artifact, directory, "evaluation.json");
            return await File.ReadAllTextAsync(Path.Combine(directory, "evaluation.json"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancellingEngine(BacktestResult result, CancellationTokenSource source) : IBacktestEngine
    {
        public BacktestResult Run(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return result;
        }
    }

    private sealed class CancellingGenerator(CancellationTokenSource source) : IQualifiedBacktestReportGenerator
    {
        private readonly BacktestReportGenerator _inner = new();

        public int Calls { get; private set; }

        public QualifiedBacktestReportResult GenerateQualified(
            BacktestResult result, BacktestReportOptions options, SamplingQualification sampling, CancellationToken cancellationToken = default)
        {
            Calls++;
            QualifiedBacktestReportResult generated = _inner.GenerateQualified(result, options, sampling, CancellationToken.None);
            source.Cancel();
            return generated;
        }
    }

    [Fact]
    public void Evaluate_RejectsFirstEvaluatedBarEarlierThanRequestedStart_AndAcceptsBoundaryAndEmptyWindow()
    {
        BacktestConfiguration configuration = Configuration();
        BacktestStrategySpecification noOp = BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>());
        BacktestInput Make(DateTime start, int historyStart) => new(
            Input(3).Bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, start, T0.AddDays(2), historyStart, historyStart);
        BacktestReportOptions OptionsFor(BacktestInput input) => new(input.Frame, input.HistoryStartIndex, input.EvaluationStartUtc, input.EvaluationEndUtc) { AnnualPeriods = 252 };
        BacktestEvaluationService service = Service(
            new ResultEngine(Result(configuration, RunStatus.Completed, new[] { 100m, 99m, 101m })),
            new NoSamplingEvidenceTrustPolicy());

        BacktestInput late = Make(T0.AddDays(1), 0);      // Bars[0] = T0 < start
        BacktestInput boundary = Make(T0, 0);              // first bar == start
        BacktestInput empty = Make(T0.AddDays(2), 3);      // no evaluated bar

        ArgumentException rejection = Assert.Throws<ArgumentException>(() => service.Evaluate(late, configuration, noOp, OptionsFor(late)));
        Assert.Equal("input", rejection.ParamName);
        Assert.NotNull(service.Evaluate(boundary, configuration, noOp, OptionsFor(boundary)));
        Assert.NotNull(service.Evaluate(empty, configuration, noOp, OptionsFor(empty)));
    }

    [Fact]
    public void PartialRunReasonCodes_ShareOneValueAcrossTheDrawdownAndBootstrapOwners()
    {
        // Two owners (drawdown result, bootstrap diagnostics) persist the same machine code for a partial run and the UI
        // resolves both through one localization key; changing one without the other must fail here.
        Assert.Equal(DrawdownReasonCodes.NotComputedPartial, BootstrapDiagnostics.PartialRunReason);
    }

    private static void AssertUnavailable(DrawdownEpisodeResult result, string reason)
    {
        Assert.Equal(DrawdownEpisodeStatus.Unavailable, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Episode);
    }

    private static void AssertSameUtc(DateTime expected, DateTime actual)
    {
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
        Assert.Equal(expected.Ticks, actual.Ticks);
    }

    private static void AssertSameDecimal(decimal expected, decimal actual) =>
        Assert.Equal(decimal.GetBits(expected), decimal.GetBits(actual));

    private static void AssertSameDrawdown(DrawdownEpisodeResult expected, ArchivedDrawdownResult actual)
    {
        Assert.Equal((expected.Status, expected.Reason), (actual.Status, actual.Reason));
        Assert.Equal(expected.Episode is null, actual.Episode is null);
        if (expected.Episode is not { } e || actual.Episode is not { } a) return;

        Assert.Equal((e.PeakIndex, e.TroughIndex, e.RecoveryIndex), (a.PeakIndex, a.TroughIndex, a.RecoveryIndex));
        AssertSameUtc(e.PeakUtc, a.PeakUtc);
        AssertSameUtc(e.TroughUtc, a.TroughUtc);
        Assert.Equal(e.RecoveryUtc?.Ticks, a.RecoveryUtc?.Ticks);
        if (a.RecoveryUtc is { } recoveryUtc) Assert.Equal(DateTimeKind.Utc, recoveryUtc.Kind);
        AssertSameDecimal(e.PeakEquity, a.PeakEquity);
        AssertSameDecimal(e.TroughEquity, a.TroughEquity);
        AssertSameDecimal(e.Depth, a.Depth);
        Assert.Equal((e.Unit, e.DeclineBars, e.RecoveryBars, e.UnderwaterBars), (a.Unit, a.DeclineBars, a.RecoveryBars, a.UnderwaterBars));
        Assert.Equal((e.DeclineDuration.Value, e.DeclineDuration.Reason), (a.DeclineDuration!.Value, a.DeclineDuration.Reason));
        Assert.Equal((e.RecoveryDuration.Value, e.RecoveryDuration.Reason), (a.RecoveryDuration!.Value, a.RecoveryDuration.Reason));
        Assert.Equal((e.UnderwaterDuration.Value, e.UnderwaterDuration.Reason), (a.UnderwaterDuration!.Value, a.UnderwaterDuration.Reason));
    }

    /// <summary>Authors the schema-1 preimage from the approved field list, independent of production writer code.</summary>
    private static string ExpectedIdentityHex(BacktestEvaluationArtifact artifact)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
        void Str(string? v)
        {
            w.Write(v is not null);
            if (v is null) return;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(v);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        void Dec(decimal v) { foreach (int part in decimal.GetBits(v)) w.Write(part); }
        void NDec(decimal? v) { w.Write(v.HasValue); if (v.HasValue) Dec(v.Value); }
        void NI32(int? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        void NI64(long? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        void NTime(DateTime? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value.Ticks); }
        void Metric(MetricValue v) { w.Write((int)v.Status); w.Write((int)v.Unit); w.Write((int)v.Reason); NDec(v.Value); }
        void Drawdown(DrawdownEpisodeResult d)
        {
            w.Write((int)d.Status);
            Str(d.Reason);
            w.Write(d.Episode is not null);
            if (d.Episode is not { } e) return;
            w.Write(e.PeakIndex); w.Write(e.TroughIndex); NI32(e.RecoveryIndex);
            w.Write(e.PeakUtc.Ticks); w.Write(e.TroughUtc.Ticks); NTime(e.RecoveryUtc);
            Dec(e.PeakEquity); Dec(e.TroughEquity); Dec(e.Depth);
            w.Write((int)e.Unit); w.Write(e.DeclineBars); NI32(e.RecoveryBars); NI32(e.UnderwaterBars);
            NI64(e.DeclineDuration.Value?.Ticks); NI64(e.RecoveryDuration.Value?.Ticks); NI64(e.UnderwaterDuration.Value?.Ticks);
            Str(e.DeclineDuration.Reason); Str(e.RecoveryDuration.Reason); Str(e.UnderwaterDuration.Reason);
        }

        BacktestEvaluationMetadata m = artifact.Metadata;
        SamplingQualification s = artifact.Sampling;
        BootstrapDiagnostics b = artifact.BootstrapDiagnostics;
        Str("StockAnalyzer.Backtest.Evaluation");
        w.Write(1); w.Write(1);
        w.Write(artifact.RunFingerprint.SchemaVersion); w.Write(artifact.RunFingerprint.ExecutionSemanticsVersion);
        Str(artifact.RunFingerprint.Sha256);
        w.Write((int)artifact.RunStatus); w.Write(0);
        Str(m.Symbol); w.Write(m.InputDataVersion); w.Write((int)m.Frame);
        w.Write(m.RequestedStartUtc.Ticks); w.Write(m.RequestedEndUtc.Ticks); NTime(m.LastProcessedTimestamp);
        w.Write(m.HistoryStartIndex); w.Write(m.TradingStartIndex); w.Write(m.SampleCount);
        w.Write(m.AnnualPeriods); Dec(m.AnnualRiskFreeRate); Dec(m.AnnualMAR); w.Write(m.BootstrapSeed); w.Write(m.BootstrapIterations); w.Write(m.FormulaVersion);
        w.Write(b.MethodVersion); NI32(b.EffectiveBlockLength); NI32(b.ValidReplicates); NI32(b.ExecutedReplicates); w.Write(b.DegenerateConstant); Str(b.Reason);
        Str(m.AssemblyIdentity); Str(m.RuntimeIdentity);
        Str(m.InputDataReference.SourceId); Str(m.InputDataReference.Version); Str(m.InputDataReference.UnavailableReason);
        w.Write((int)s.Status); w.Write((int)s.Reason);
        Str(s.ProviderId); Str(s.CalendarId); Str(s.CalendarVersion); Str(s.ExchangeId); Str(s.TimeZoneId);
        w.Write(s.TimestampConvention.HasValue); if (s.TimestampConvention.HasValue) w.Write((int)s.TimestampConvention.Value);
        w.Write((int)s.Frame); NI32(s.ExpectedCount); w.Write(s.ObservedCount); Str(s.ExpectedTimestampsDigest);

        BacktestReport? r = artifact.Report;
        w.Write(r is not null);
        if (r is not null)
        {
            foreach (MetricValue metric in new[]
            {
                r.TotalPnL, r.MaxDrawdownAmount, r.ExpectedPayoff, r.TotalReturn, r.CAGR, r.WinRate, r.ProfitFactor, r.MaxDrawdown, r.UlcerIndex,
                r.BarSharpe, r.AnnualizedSharpe, r.AnnualizedSharpeAutocorrelationAdjusted, r.BarSortino, r.AnnualizedSortino,
                r.AnnualizedSortinoAutocorrelationAdjusted, r.CalmarFullPeriod, r.SQN, r.RecoveryFactor,
            })
            {
                Metric(metric);
            }
            ConfidenceInterval? ci = r.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval;
            w.Write(ci.HasValue);
            if (ci is { } interval) { Dec(interval.Lower); Dec(interval.Upper); }
            w.Write(r.TotalTrades); w.Write(r.WinTrades); w.Write(r.LossTrades); w.Write(r.BreakevenTrades); w.Write(r.SqnWarning);
        }
        Drawdown(artifact.RatioDrawdown);
        Drawdown(artifact.AmountDrawdown);
        w.Flush();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));
    }

    private static BacktestResult ResultAt(BacktestConfiguration configuration, IReadOnlyList<decimal> equity, IReadOnlyList<DateTime> timestamps)
    {
        var points = ImmutableArray.CreateBuilder<EquityPoint>(equity.Count);
        for (int index = 0; index < equity.Count; index++)
        {
            points.Add(new EquityPoint(index, timestamps[index], equity[index], equity[index], 0m, 0m));
        }
        return new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty, ImmutableArray<BacktestFill>.Empty, ImmutableArray<BacktestTrade>.Empty,
            points.MoveToImmutable(), ImmutableArray<BacktestSignal>.Empty, configuration, RunStatus.Completed, "NoOp", new byte[32], false);
    }

    private sealed class CountingEngine(BacktestResult result) : IBacktestEngine
    {
        public int Runs { get; private set; }

        public BacktestResult Run(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy, CancellationToken cancellationToken = default)
        {
            Runs++;
            return result;
        }
    }

    private sealed class CountingGenerator : IQualifiedBacktestReportGenerator
    {
        private readonly BacktestReportGenerator _inner = new();

        public int Calls { get; private set; }

        public QualifiedBacktestReportResult GenerateQualified(
            BacktestResult result, BacktestReportOptions options, SamplingQualification sampling, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.GenerateQualified(result, options, sampling, cancellationToken);
        }
    }

    private static void AssertEpisode(
        DrawdownEpisodeResult result,
        int peak,
        int trough,
        int recovery,
        int declineBars,
        int recoveryBars,
        int underwaterBars)
    {
        Assert.Equal(DrawdownEpisodeStatus.Available, result.Status);
        Assert.Equal(peak, result.Episode!.PeakIndex);
        Assert.Equal(trough, result.Episode.TroughIndex);
        Assert.Equal(recovery, result.Episode.RecoveryIndex);
        Assert.Equal(declineBars, result.Episode.DeclineBars);
        Assert.Equal(recoveryBars, result.Episode.RecoveryBars);
        Assert.Equal(underwaterBars, result.Episode.UnderwaterBars);
    }

    private static BacktestEvaluationService Service(IBacktestEngine engine, ISamplingEvidenceTrustPolicy trust) =>
        new(engine, new BacktestReportGenerator(), trust);

    private static BacktestEvaluationArtifact Evaluate(
        BacktestResult result,
        BacktestInput input,
        BacktestConfiguration configuration,
        BacktestReportOptions options,
        SamplingEvidence evidence) => Service(
            new ResultEngine(result),
            new ReferenceTrustPolicy(evidence)).Evaluate(
                input,
                configuration,
                BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
                options,
                evidence);

    private static BacktestConfiguration Configuration(decimal initialCapital = 1_000m) => new()
    {
        InitialCapital = initialCapital,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
    };

    private static BacktestInput Input(int count)
    {
        ImmutableArray<CandleData> bars = Enumerable.Range(0, count)
            .Select(i => new CandleData(T0.AddDays(i), 100m, 101m, 99m, 100m, 1_000L))
            .ToImmutableArray();
        return new BacktestInput(
            bars,
            "TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            T0,
            T0.AddDays(Math.Max(0, count - 1)),
            0,
            0);
    }

    private static BacktestInput Input(ImmutableArray<DateTime> timestamps)
    {
        ImmutableArray<CandleData> bars = timestamps
            .Select(timestamp => new CandleData(timestamp, 100m, 101m, 99m, 100m, 1_000L))
            .ToImmutableArray();
        return new BacktestInput(
            bars,
            "TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            timestamps[0],
            timestamps[^1],
            0,
            0);
    }

    private static BacktestReportOptions Options(BacktestInput input, int seed) => new(
        input.Frame,
        input.HistoryStartIndex,
        input.EvaluationStartUtc,
        input.EvaluationEndUtc)
    {
        AnnualPeriods = 252,
        BootstrapSeed = seed,
        BootstrapIterations = 1000,
    };

    private static decimal[] Equity(int count) => Enumerable.Range(0, count)
        .Select(i => (i % 3) switch { 0 => 1_010m, 1 => 990m, _ => 1_020m })
        .ToArray();

    private static BacktestResult Result(
        BacktestConfiguration configuration,
        RunStatus status,
        IReadOnlyList<decimal> equity)
    {
        var pointBuilder = ImmutableArray.CreateBuilder<EquityPoint>(equity.Count);
        for (int index = 0; index < equity.Count; index++)
        {
            decimal value = equity[index];
            pointBuilder.Add(new EquityPoint(index, T0.AddDays(index), value, value, 0m, 0m));
        }
        ImmutableArray<EquityPoint> points = pointBuilder.MoveToImmutable();
        return new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            ImmutableArray<BacktestTrade>.Empty,
            points,
            ImmutableArray<BacktestSignal>.Empty,
            configuration,
            status,
            "NoOp",
            new byte[32],
            false);
    }

    private static SamplingEvidence Evidence(
        BacktestInput input,
        ImmutableArray<DateTime> timestamps,
        bool complete = true) => new(
            "fixture-provider",
            "fixture-calendar",
            "v1",
            "XTEST",
            "UTC",
            input.Symbol,
            input.Frame,
            TimestampConvention.PeriodEnd,
            input.EvaluationStartUtc,
            input.EvaluationEndUtc,
            complete,
            timestamps);

    private sealed class ResultEngine(BacktestResult result) : IBacktestEngine
    {
        public BacktestResult Run(
            BacktestInput input,
            BacktestConfiguration configuration,
            IBacktestStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private sealed class ReferenceTrustPolicy(SamplingEvidence approved) : ISamplingEvidenceTrustPolicy
    {
        public bool IsApproved(SamplingEvidence evidence) => ReferenceEquals(approved, evidence);
    }
}
