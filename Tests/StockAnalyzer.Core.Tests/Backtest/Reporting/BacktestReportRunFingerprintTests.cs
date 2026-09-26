#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Owner decision G5 (adoption): the run's fingerprint - or the reason none exists - travels with the report and its JSON export. The frozen builder rides on
/// <see cref="BacktestReportOptions"/>; the generator seals it against the result, so a result of another run is refused.
/// </summary>
public class BacktestReportRunFingerprintTests
{
    // Zig-zag closes so "Close > SMA(3)" / "Close < SMA(3)" trade (a report needs a real run, not a hand-built result).
    private static readonly decimal[] Closes = { 100m, 100m, 100m, 110m, 112m, 90m, 88m, 100m, 110m, 95m };

    private static ImmutableArray<CandleData> Bars()
    {
        var bars = new List<CandleData>();
        for (int i = 0; i < Closes.Length; i++)
        {
            decimal open = i == 0 ? Closes[0] : Closes[i - 1];
            bars.Add(SyntheticBars.Bar(i, open, Math.Max(open, Closes[i]) + 1m, Math.Min(open, Closes[i]) - 1m, Closes[i]));
        }
        return bars.ToImmutableArray();
    }

    private static BacktestConfiguration Config(decimal initialCapital = 1000m) => VerificationHarness.MakeConfig(initialCapital: initialCapital, sizingParameter: 3m);

    private static BacktestReportOptions Options(BacktestRunFingerprintBuilder? builder)
        => new(TimeFrame.D1, 0, SyntheticBars.Start, SyntheticBars.Start.AddDays(Closes.Length + 1))
        {
            AnnualPeriods = 252,
            RunFingerprintBuilder = builder,
        };

    /// <summary>The intended sequence: freeze BEFORE the run, run the real engine, generate (which seals).</summary>
    private static (BacktestReport Report, BacktestRunFingerprintBuilder Builder, BacktestResult Result) Generate(IBacktestStrategy strategy, BacktestConfiguration? config = null)
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        config ??= Config();
        BacktestRunFingerprintBuilder builder = BacktestRunFingerprintBuilder.Freeze(input, config, strategy);
        BacktestResult result = VerificationHarness.CreateEngine().Run(input, config, strategy);
        return (new BacktestReportGenerator().Generate(result, Options(builder)), builder, result);
    }

    [Fact]
    public void ABuiltInStrategy_ReportCarriesTheAvailableFingerprint_EqualToTheSealedOne()
    {
        var (report, builder, result) = Generate(VerificationHarness.SmaTrendStrategy(3));

        BacktestReportRunFingerprint fingerprint = Assert.IsType<BacktestReportRunFingerprint>(report.RunFingerprint);
        BacktestRunFingerprint sealedDirectly = builder.Seal(result);
        Assert.True(fingerprint.IsAvailable);
        Assert.Equal(sealedDirectly.ToHexString(), fingerprint.Sha256);
        Assert.Equal(64, fingerprint.Sha256!.Length);
        Assert.Equal(sealedDirectly.SchemaVersion, fingerprint.SchemaVersion);
        Assert.Equal(BacktestExecutionSemantics.Version, fingerprint.ExecutionSemanticsVersion);
        Assert.Null(fingerprint.UnavailableReason);
    }

    [Fact]
    public void ACustomStrategy_ReportCarriesTheReason_NeverAnEmptyOrSubstitutedHash()
    {
        var (report, _, _) = Generate(new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = VerificationHarness.Req(SignalType.LongEntry) }));

        BacktestReportRunFingerprint fingerprint = Assert.IsType<BacktestReportRunFingerprint>(report.RunFingerprint);
        Assert.False(fingerprint.IsAvailable);
        Assert.Null(fingerprint.Sha256);
        Assert.Contains(typeof(ScriptedStrategy).FullName!, fingerprint.UnavailableReason);
    }

    [Fact]
    public void WithoutAFrozenBuilder_ReportHasNoFingerprintItem_AndEverythingElseIsUnchanged()
    {
        BacktestResult result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m });

        BacktestReport withoutBuilder = new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());

        Assert.Null(withoutBuilder.RunFingerprint);
    }

    [Fact]
    public void AResultOfAnotherRun_IsRefused_InsteadOfBorrowingTheFrozenIdentity()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        BacktestRunFingerprintBuilder builder = BacktestRunFingerprintBuilder.Freeze(input, Config(), VerificationHarness.SmaTrendStrategy(3));
        BacktestResult foreign = VerificationHarness.CreateEngine().Run(input, Config(initialCapital: 2000m), VerificationHarness.SmaTrendStrategy(3));

        Assert.Throws<ArgumentException>(() => new BacktestReportGenerator().Generate(foreign, Options(builder)));
    }

    [Fact]
    public async Task Export_RoundTripsTheAvailableFingerprint_AndTheHashIsInTheJsonText()
    {
        var (report, _, _) = Generate(VerificationHarness.SmaTrendStrategy(3));
        string fileName = "test_export_" + Guid.NewGuid().ToString("N") + ".json";
        string path = PathDiscovery.ResolveBacktestReportExportPath(fileName);

        try
        {
            await new BacktestReportExporter().ExportAsync(report, fileName);

            string json = await File.ReadAllTextAsync(path);
            Assert.Contains(report.RunFingerprint!.Sha256!, json); // the digest itself is exported (a default serialization of the engine type would drop it)
            BacktestReport? loaded = await AtomicJsonFile.LoadAsync<BacktestReport?>(path);
            Assert.Equal(report.RunFingerprint, loaded!.RunFingerprint);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_RoundTripsTheUnavailableReason_AndAMissingFingerprintStaysNull()
    {
        var (unavailable, _, _) = Generate(new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>()));
        BacktestReport missing = new BacktestReportGenerator().Generate(ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m }), ReportTestHelpers.Options());
        string unavailablePath = PathDiscovery.ResolveBacktestReportExportPath("test_export_" + Guid.NewGuid().ToString("N") + ".json");
        string missingPath = PathDiscovery.ResolveBacktestReportExportPath("test_export_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            await AtomicJsonFile.SaveAsync(unavailablePath, unavailable);
            await AtomicJsonFile.SaveAsync(missingPath, missing);

            BacktestReport? loadedUnavailable = await AtomicJsonFile.LoadAsync<BacktestReport?>(unavailablePath);
            BacktestReport? loadedMissing = await AtomicJsonFile.LoadAsync<BacktestReport?>(missingPath);
            Assert.Equal(unavailable.RunFingerprint, loadedUnavailable!.RunFingerprint);
            Assert.False(loadedUnavailable.RunFingerprint!.IsAvailable);
            Assert.Null(loadedMissing!.RunFingerprint);
        }
        finally
        {
            if (File.Exists(unavailablePath)) File.Delete(unavailablePath);
            if (File.Exists(missingPath)) File.Delete(missingPath);
        }
    }
}
