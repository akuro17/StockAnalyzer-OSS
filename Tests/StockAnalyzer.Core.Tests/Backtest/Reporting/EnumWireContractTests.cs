using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Pins the integer wire values of every enum that is persisted in report JSON or folded into the evaluation identity hash
/// (Y:\Temp\sa_implementation_plan_BacktestReportIntegrityContracts.md, T1). The literal tables below are the contract:
/// reflection cannot tell an explicit enum value from an implicit one, so a "all members are explicit" test is not possible.
/// Reordering, inserting, renaming or removing a member fails a table test; changing the byte layout fails a golden test.
/// </summary>
public class EnumWireContractTests
{
    private static Dictionary<string, int> Table<T>() where T : struct, Enum =>
        Enum.GetValues<T>().ToDictionary(value => value.ToString(), value => Convert.ToInt32(value));

    [Fact]
    public void MetricStatus_WireValuesArePinned() => Assert.Equal(
        new Dictionary<string, int>
        {
            ["Valid"] = 0, ["InsufficientData"] = 1, ["NotApplicable"] = 2, ["Undefined"] = 3, ["PositiveInfinity"] = 4, ["NumericFailure"] = 5,
        },
        Table<MetricStatus>());

    [Fact]
    public void MetricUnit_WireValuesArePinned() => Assert.Equal(
        new Dictionary<string, int>
        {
            ["ReturnRatio"] = 0, ["DrawdownRatio"] = 1, ["WinRateRatio"] = 2, ["Dimensionless"] = 3, ["Currency"] = 4,
            ["Bars"] = 5, ["PercentPoints"] = 6, ["Count"] = 7, ["ExposureRatio"] = 8,
        },
        Table<MetricUnit>());

    [Fact]
    public void MetricReason_WireValuesArePinned() => Assert.Equal(
        new Dictionary<string, int>
        {
            ["None"] = 0, ["EmptyInput"] = 1, ["SingleElement"] = 2, ["NoClosedTrades"] = 3, ["ZeroDivisor"] = 4,
            ["NegativeEquityInPeriod"] = 5, ["ZeroPeriod"] = 6, ["NegativeFinalEquity"] = 7, ["RiskDataMissing"] = 8,
            ["AllBreakeven"] = 9, ["DownsideZero"] = 10, ["SampleTooSmall"] = 11, ["ArithmeticOverflow"] = 12,
            ["NonFiniteResult"] = 13, ["UnexpectedZeroDivisor"] = 14, ["SamplingUnverified"] = 15, ["SamplingRejected"] = 16,
            ["EvaluationCoverageUnverified"] = 17, ["NoWinningTrades"] = 18, ["NoLosingTrades"] = 19,
        },
        Table<MetricReason>());

    [Fact]
    public void RunStatus_WireValuesArePinned() => Assert.Equal(
        new Dictionary<string, int> { ["Completed"] = 0, ["Cancelled"] = 1, ["Failed"] = 2, ["Insolvent"] = 3 },
        Table<RunStatus>());

    [Fact]
    public void TimeFrame_WireValuesArePinned() => Assert.Equal(
        new Dictionary<string, int>
        {
            ["M1"] = 0, ["M5"] = 1, ["M15"] = 2, ["M30"] = 3, ["H1"] = 4, ["H4"] = 5, ["D1"] = 6, ["W1"] = 7, ["MN1"] = 8,
        },
        Table<TimeFrame>());

    [Fact]
    public void EnumsAlreadyExplicitElsewhereInTheIdentityAreStillPinned()
    {
        Assert.Equal(
            new Dictionary<string, int> { ["Verified"] = 0, ["Unverified"] = 1, ["Rejected"] = 2 }, Table<SamplingStatus>());
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["None"] = 0, ["NoEvidence"] = 1, ["UntrustedEvidence"] = 2, ["IncompleteEvidence"] = 3, ["InvalidEvidence"] = 4, ["MissingOrUnexpectedBar"] = 5,
            },
            Table<SamplingReason>());
        Assert.Equal(new Dictionary<string, int> { ["PeriodStart"] = 0, ["PeriodEnd"] = 1 }, Table<TimestampConvention>());
        Assert.Equal(
            new Dictionary<string, int> { ["Available"] = 0, ["NoDrawdown"] = 1, ["Unavailable"] = 2, ["NotComputedPartial"] = 3 },
            Table<DrawdownEpisodeStatus>());
    }

    // ---- Golden bytes: one MetricValue segment, authored as literals without casting the enums -----------------------------------

    public static IEnumerable<object[]> MetricSegments()
    {
        // status(Int32) unit(Int32) reason(Int32) hasValue(1 byte) [decimal: lo mid hi flags, each Int32]; everything little-endian.
        yield return new object[]
        {
            MetricValue.Valid(1.5m, MetricUnit.Currency),
            "00000000" + "04000000" + "00000000" + "01" + "0F000000" + "00000000" + "00000000" + "00000100",
        };
        yield return new object[]
        {
            MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Count, MetricReason.NoClosedTrades),
            "01000000" + "07000000" + "03000000" + "00",
        };
        yield return new object[]
        {
            MetricValue.NonValid(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.NoLosingTrades),
            "04000000" + "03000000" + "13000000" + "00",
        };
        yield return new object[]
        {
            MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.ExposureRatio, MetricReason.NoWinningTrades),
            "03000000" + "08000000" + "12000000" + "00",
        };
        yield return new object[]
        {
            MetricValue.NonValid(MetricStatus.NumericFailure, MetricUnit.PercentPoints, MetricReason.EvaluationCoverageUnverified),
            "05000000" + "06000000" + "11000000" + "00",
        };
        yield return new object[]
        {
            MetricValue.NonValid(MetricStatus.NotApplicable, MetricUnit.Bars, MetricReason.SamplingRejected),
            "02000000" + "05000000" + "10000000" + "00",
        };
        yield return new object[]
        {
            MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.WinRateRatio, MetricReason.ZeroDivisor),
            "03000000" + "02000000" + "04000000" + "00",
        };
    }

    [Theory]
    [MemberData(nameof(MetricSegments))]
    public void MetricSegment_EncodesToPinnedBytes(MetricValue metric, string expectedHex)
    {
        using var writer = new CanonicalWriter();
        BacktestReportIdentityEncoder.WriteMetric(writer, metric);

        Assert.Equal(expectedHex, Convert.ToHexString(writer.ToArray()));
    }

    // ---- Golden vectors: a fully synthetic report + fixed inputs, so no calculator or machine identity is involved ---------------

    private static readonly DateTime StartUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MetricValue V(decimal value, MetricUnit unit) => MetricValue.Valid(value, unit);

    private static BacktestReport SyntheticReport(bool withExtendedMetrics) => new()
    {
        TotalPnL = V(1234.5m, MetricUnit.Currency),
        MaxDrawdownAmount = V(210m, MetricUnit.Currency),
        ExpectedPayoff = V(12.25m, MetricUnit.Currency),
        TotalReturn = V(0.125m, MetricUnit.ReturnRatio),
        CAGR = V(0.1m, MetricUnit.ReturnRatio),
        WinRate = V(0.6m, MetricUnit.WinRateRatio),
        ProfitFactor = V(1.8m, MetricUnit.Dimensionless),
        MaxDrawdown = V(0.05m, MetricUnit.DrawdownRatio),
        UlcerIndex = V(2.5m, MetricUnit.PercentPoints),
        BarSharpe = V(0.1m, MetricUnit.Dimensionless),
        AnnualizedSharpe = V(1.6m, MetricUnit.Dimensionless),
        AnnualizedSharpeAutocorrelationAdjusted = V(1.4m, MetricUnit.Dimensionless),
        BarSortino = V(0.2m, MetricUnit.Dimensionless),
        AnnualizedSortino = V(3.2m, MetricUnit.Dimensionless),
        AnnualizedSortinoAutocorrelationAdjusted = MetricValue.NonValid(
            MetricStatus.InsufficientData, MetricUnit.Dimensionless, MetricReason.SampleTooSmall),
        CalmarFullPeriod = V(2m, MetricUnit.Dimensionless),
        SQN = V(1.1m, MetricUnit.Dimensionless),
        RecoveryFactor = V(5.9m, MetricUnit.Dimensionless),
        TotalTrades = 10,
        WinTrades = 6,
        LossTrades = 3,
        BreakevenTrades = 1,
        SqnWarning = true,
        GrossProfit = withExtendedMetrics ? V(900m, MetricUnit.Currency) : null,
        GrossLoss = withExtendedMetrics ? V(300m, MetricUnit.Currency) : null,
        AverageWin = withExtendedMetrics ? V(150m, MetricUnit.Currency) : null,
        AverageLoss = withExtendedMetrics ? V(100m, MetricUnit.Currency) : null,
        PayoffRatio = withExtendedMetrics ? V(1.5m, MetricUnit.Dimensionless) : null,
        LargestWin = withExtendedMetrics ? V(400m, MetricUnit.Currency) : null,
        LargestLoss = withExtendedMetrics ? V(150m, MetricUnit.Currency) : null,
        AverageHoldingPeriod = withExtendedMetrics ? V(3.5m, MetricUnit.Bars) : null,
        MaxConsecutiveWins = withExtendedMetrics ? V(4m, MetricUnit.Count) : null,
        MaxConsecutiveLosses = withExtendedMetrics ? V(2m, MetricUnit.Count) : null,
        MaxDepthDrawdownDuration = withExtendedMetrics ? V(7m, MetricUnit.Bars) : null,
        LongestDrawdownDuration = withExtendedMetrics ? V(9m, MetricUnit.Bars) : null,
        TimeInMarket = withExtendedMetrics ? V(40m, MetricUnit.Bars) : null,
        Exposure = withExtendedMetrics ? V(0.8m, MetricUnit.ExposureRatio) : null,
        ExposureAdjustedCAGR = withExtendedMetrics
            ? MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.ReturnRatio, MetricReason.ZeroDivisor)
            : null,
        MaxDepthDrawdownDurationRightCensored = withExtendedMetrics ? false : null,
        LongestDrawdownDurationRightCensored = withExtendedMetrics ? true : null,
    };

    private static string GoldenIdentityHex(bool withExtendedMetrics, out int schemaVersion)
    {
        var metadata = new BacktestEvaluationMetadata(
            "GOLD", 1, TimeFrame.D1, StartUtc, StartUtc.AddDays(49), StartUtc.AddDays(49),
            0, 0, 50, 252, 0.02m, 0m, 7, 1000, BacktestReport.CurrentFormulaVersion,
            "golden-assembly", "golden-runtime", InputDataReference.Unavailable());
        var fingerprint = new BacktestReportRunFingerprint
        {
            IsAvailable = true,
            Sha256 = new string('A', 64),
            SchemaVersion = 1,
            ExecutionSemanticsVersion = 2,
        };
        var sampling = new SamplingQualification(SamplingStatus.Unverified, SamplingReason.NoEvidence, TimeFrame.D1, 50, null, null);
        var bootstrap = new BootstrapDiagnostics(BootstrapDiagnostics.CurrentMethodVersion, null, null, null, false, BootstrapDiagnostics.LegacyEntryPointReason);
        var noDrawdown = new DrawdownEpisodeResult(DrawdownEpisodeStatus.NoDrawdown, "NoDrawdown", null);

        EvaluationContentIdentity identity = BacktestReportIdentityEncoder.Compute(
            fingerprint, metadata, RunStatus.Completed, sampling, bootstrap, SyntheticReport(withExtendedMetrics), noDrawdown, noDrawdown);

        Assert.True(identity.IsAvailable);
        schemaVersion = identity.SchemaVersion;
        return identity.Sha256!;
    }

    // Both literals were first computed by an independent script that authored the preimage bytes from the field list (not by production
    // code) and then matched against the encoder. A change here means the persisted identity of existing reports changed: it must be a
    // deliberate schema decision, never a side effect. The schema-2 literal is regenerated on purpose whenever the extended-metric block
    // is extended (e.g. renamed metrics or added flags in the drawdown-censoring plan).
    private const string GoldenSchema1Hex = "28E5A3731F8E303C99C69722C51794E2425C33252CBE5FCC4982B4DFA0381448";
    private const string GoldenSchema2Hex = "F9353074C832C826C3FBB7C691964AE8487D8015C074CE764FD267ACD6C64A55";

    [Fact]
    public void GoldenVector_SchemaVersion1_IsPinned()
    {
        string hex = GoldenIdentityHex(withExtendedMetrics: false, out int schemaVersion);

        Assert.Equal(1, schemaVersion);
        Assert.Equal(GoldenSchema1Hex, hex);
    }

    [Fact]
    public void GoldenVector_SchemaVersion2_IsPinned()
    {
        string hex = GoldenIdentityHex(withExtendedMetrics: true, out int schemaVersion);

        Assert.Equal(2, schemaVersion);
        Assert.Equal(GoldenSchema2Hex, hex);
    }
}
