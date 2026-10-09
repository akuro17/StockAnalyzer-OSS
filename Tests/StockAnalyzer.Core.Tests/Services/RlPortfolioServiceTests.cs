using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The seam the portfolio dialog uses: settings validation at construction, the preflight against an isolated Data root with real local
/// Parquet files, the panel folder next to the run evidence, and (opt-in, same switch as the training smoke) one real run through the service.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlPortfolioServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_service_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private string DataRoot => Path.Combine(_root, "data");

    public RlPortfolioServiceTests()
    {
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, DataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, _previousRoot);
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, _previousEnabled);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void SettingsAreValidatedWhenTheServiceIsCreated()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new RlPortfolioService(new RlPortfolioSettings { MaxSymbols = 1 }));
        Assert.Equal("MaxSymbols", error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RlPortfolioService(new RlPortfolioSettings { HoldoutPercent = 0 }));
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioService(null!));
        Assert.Empty(new RlPortfolioService(new RlPortfolioSettings()).Warnings);
        var warned = new RlPortfolioService(new RlPortfolioSettings { MaxSymbols = RlPortfolioLimits.StandardMaximumSymbols + 1 });
        Assert.Contains(warned.Warnings, w => w.Contains("MaxSymbols"));          // between the bands: runs, with one warning
    }

    [Fact]
    public async Task ThePreflightReadsTheLocalDataFilesOfTheDataRoot()
    {
        foreach (string symbol in new[] { "AAA", "BBB" }) RlSourceFixture.WriteLocalSymbol(DataRoot, symbol, RlSourceFixture.FirstDay, 1_400);
        var service = new RlPortfolioService(new RlPortfolioSettings());
        var result = await service.PreflightAsync(new[] { "BBB", "AAA", "ZZZ" }, RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(1_400),
            holdoutRequested: true, RlPanelEvaluationMode.ShortHistory, default);
        Assert.Equal(new[] { ("AAA", RlPanelSymbolStatus.Ready), ("BBB", RlPanelSymbolStatus.Ready), ("ZZZ", RlPanelSymbolStatus.MissingFile) },
            result.Symbols.Select(s => (s.Symbol, s.Status)).ToArray());
        Assert.True(result.Feasible);
        Assert.Equal(1_400, result.CalendarLength);
        Assert.Same(service.Settings, service.Settings);
    }

    [Fact]
    public void ThePanelFolderSitsBesideTheRunsAndIsNotListedAsARun()
    {
        var store = new RlPortfolioArtifactStore(Path.Combine(_root, "artifacts"));
        var id = Guid.NewGuid();
        string panel = store.PanelDirectory(id);
        Assert.Equal(Path.Combine(store.Root, "panels", id.ToString("N")), panel);
        Directory.CreateDirectory(panel);
        Assert.Empty(store.List());
        Assert.Throws<ArgumentException>(() => store.PanelDirectory(Guid.Empty));
    }

    [RlPortfolioTrainingSmokeTests.SmokeFact]
    public async Task OneRealRunThroughTheServiceKeepsThePanelAndPublishesTheValidatedEvidence()
    {
        const int days = 1_400;
        var symbols = new[] { "WAVA", "WAVB", "WAVC", "WAVD" };
        for (int i = 0; i < symbols.Length; i++)
        {
            string close = $"(100 + 10 * sin(range / 7.0 + {i * 2}) + range / 40.0)";
            string daily = Path.Combine(DataRoot, "Daily"), meta = Path.Combine(DataRoot, "Metadata");
            Directory.CreateDirectory(daily); Directory.CreateDirectory(meta);
            RlSourceFixture.CreateParquet(Path.Combine(daily, symbols[i] + ".parquet"),
                $"SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date,({close}*(1+0.002*sin(range)))::FLOAT AS open,({close}+2)::FLOAT AS high,({close}-2)::FLOAT AS low,{close}::FLOAT AS close,1000::BIGINT AS volume FROM range({days})");
            RlSourceFixture.CreateParquet(Path.Combine(meta, symbols[i] + ".meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        }
        string artifacts = Path.Combine(_root, "artifacts");
        var service = new RlPortfolioService(new RlPortfolioSettings(), dataRoot: artifacts);
        var costs = new BacktestConfiguration { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 100_000m,
            SlippageRatio = 0.001m, CommissionFlat = 1m, CommissionPerUnit = 0.005m };
        var request = new RlPortfolioRunRequest(symbols.ToImmutableArray(), RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(days), true,
            RlPanelEvaluationMode.ShortHistory, costs, SmokeOnly: true);
        var preflight = await service.PreflightAsync(symbols, request.Start, request.EndExclusive, true, request.Mode, default);
        Assert.True(preflight.Feasible);
        var result = await service.RunAsync(request, null, default);
        var store = new RlPortfolioArtifactStore(artifacts);
        Assert.StartsWith(Path.Combine(store.Root, "panels") + Path.DirectorySeparatorChar, result.PanelDirectory);
        Assert.True(File.Exists(Path.Combine(result.PanelDirectory, RlPortfolioContract.PanelManifest)));
        Assert.Equal(RlPortfolioArtifactStatus.Verified, Assert.Single(store.List()).Status);
        var panel = RlPanelSnapshot.Load(result.PanelDirectory, RlSourceFixture.Limits, service.Settings, RlPanelEvaluationMode.ShortHistory);
        RlPortfolioEvaluationReports.Validate(RlPortfolioEvaluationReports.Read(result.ReportPath), panel.Manifest, RlPanelEvaluationMode.ShortHistory,
            StockAnalyzer.Core.Common.ContentHash.OfFile(result.ModelPath), RlContract.CreateConfiguration(costs), service.Settings.MaxWeightPercent);
        Assert.Equal(4, result.Evaluation.Symbols.Length);
    }
}
