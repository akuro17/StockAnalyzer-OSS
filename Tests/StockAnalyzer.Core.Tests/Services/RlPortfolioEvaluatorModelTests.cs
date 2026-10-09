using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The public evaluation entry point on a real panel snapshot (local Parquet, isolated Data root) and the linear ONNX fixture actor (three slots,
/// not a trained model). The policy arm is recomposed by hand from the tested parts (observation builder, actor, weights, session); the baselines
/// and the report are checked by the report validator.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlPortfolioEvaluatorModelTests : IDisposable
{
    private const int Days = 1_400;
    private static readonly string[] Symbols = { "WAVA", "WAVB", "WAVC" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_evalmodel_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private string DataRoot => Path.Combine(_root, "data");

    public RlPortfolioEvaluatorModelTests()
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

    private static BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
    {
        SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 100_000m,
        SlippageRatio = 0.001m, CommissionFlat = 1m, CommissionPerUnit = 0.005m,
    });

    private void WriteWave(string symbol, int phase)
    {
        string close = $"(100 + 10 * sin(range / 7.0 + {phase}) + range / 40.0)";
        string daily = Path.Combine(DataRoot, "Daily"), meta = Path.Combine(DataRoot, "Metadata");
        Directory.CreateDirectory(daily); Directory.CreateDirectory(meta);
        RlSourceFixture.CreateParquet(Path.Combine(daily, symbol + ".parquet"),
            $"SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date,({close}*(1+0.002*sin(range)))::FLOAT AS open,({close}+2)::FLOAT AS high,({close}-2)::FLOAT AS low,{close}::FLOAT AS close,1000::BIGINT AS volume FROM range({Days})");
        RlSourceFixture.CreateParquet(Path.Combine(meta, symbol + ".meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
    }

    private async Task<RlPanelSnapshot> Panel(RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory)
    {
        for (int i = 0; i < Symbols.Length; i++) WriteWave(Symbols[i], i * 2);
        return await RlPanelSnapshotFactory.PrepareAsync(Symbols, RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(Days),
            Path.Combine(_root, "panel"), RlSourceFixture.Limits, new RlPortfolioSettings(), holdoutEnabled: false, mode);
    }

    private string Model(RlPanelSnapshot panel, Action<Dictionary<string, string>>? change = null, RlPortfolioObservationLayout? layout = null)
    {
        layout ??= RlPortfolioObservationLayout.V1;
        string directory = Path.Combine(_root, "model_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "policy.onnx");
        var metadata = new Dictionary<string, string>
        {
            [RlPortfolioContract.MetadataContractHash] = layout.ContractHash,
            [RlPortfolioContract.MetadataTargetType] = RlPortfolioContract.TargetType,
            [RlPortfolioContract.MetadataMaxSymbols] = "3",
            ["window_size"] = "64",
            [RlPortfolioContract.MetadataChannels] = layout.MarketChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [RlPortfolioContract.MetadataUniverseSha256] = panel.Manifest.UniverseSha256,
            [RlPortfolioContract.MetadataCalendarSha256] = panel.Manifest.CalendarSha256,
            [RlPortfolioContract.MetadataEvaluationSpecification] = RlPortfolioEvaluator.SpecificationId(panel.Catalog.Mode),
            ["scaler_ref"] = "policy.onnx.scaler.json",
        };
        change?.Invoke(metadata);
        string fixture = layout == RlPortfolioObservationLayout.V1 ? "rl_portfolio_fixture_n3.onnx" : "rl_portfolio_fixture_v2a_n3.onnx";
        OnnxMetadataFixture.Write(Path.Combine(AppContext.BaseDirectory, "Assets", fixture), path, metadata);
        RlPortfolioScaler.Save(path, RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(0d, 0.05d, -1d, 1d), layout.MarketChannelCount).ToArray(), layout));
        return path;
    }

    /// <summary>
    /// A v2-a model is evaluated through the same protocol: the report names its observation contract, validates against it only, and the policy arm is the
    /// hand-recomposed one (the builder reads the layout from the scaler of the model).
    /// </summary>
    [Fact]
    public async Task AV2ModelIsEvaluatedUnderItsOwnContractAndTheReportValidatesOnlyAgainstIt()
    {
        var panel = await Panel();
        string model = Model(panel, layout: RlPortfolioObservationLayout.V2A);
        var costs = Costs();
        var report = RlPortfolioEvaluator.Evaluate(panel, model, costs, 100, smokeOnly: false);
        Assert.Equal(RlPortfolioV2Contract.ContractHash, report.PortfolioContractHash);
        Assert.Equal(RlPortfolioContract.SpecificationId, report.SpecificationId);
        RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, report.ModelSha256, costs, 100, layout: RlPortfolioObservationLayout.V2A);
        Assert.Throws<InvalidDataException>(() =>
            RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, report.ModelSha256, costs, 100));

        using var actor = new RlPortfolioActorModel(model);
        Assert.Same(RlPortfolioObservationLayout.V2A, actor.Layout);
        var block = panel.Catalog.CellBlocks(RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Train)[0];
        var tradable = panel.Catalog.EligibleIn(block, RlPanelSymbolGroup.Train);
        using var session = new RlPortfolioSession(panel, block, tradable, costs);
        var snapshot = session.Reset();
        for (int i = 0; i < RlContract.EpisodeHorizon; i++)
        {
            float[] observation = RlPortfolioObservationBuilder.Build(panel, 3, snapshot, tradable, actor.Scaler);
            Assert.Equal(1354, observation.Length);
            var weights = actor.Decide(observation, RlPortfolioObservationBuilder.SlotMask(panel.SymbolCount, 3, tradable), 100);
            snapshot = session.Step(weights.Assets.Take(panel.SymbolCount).ToImmutableArray()).Snapshot;
        }
        Assert.Equal((snapshot.Equity - costs.InitialCapital) / costs.InitialCapital, report.Cells[0].Blocks[0].PolicyReturn);

        // A v1 report of the same panel is another contract: the strict comparison refuses the pair, the ablation option accepts it.
        var v1Report = RlPortfolioEvaluator.Evaluate(panel, Model(panel), costs, 100, smokeOnly: false);
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(report, v1Report));
        Assert.True(RlPortfolioTransferComparison.Compare(report, v1Report, acrossModelContracts: true).AcrossModelContracts);
    }

    [Fact]
    public async Task TheModelIsEvaluatedOnEveryCellAndTheReportValidates()
    {
        var panel = await Panel();
        string model = Model(panel);
        var costs = Costs();
        var report = RlPortfolioEvaluator.Evaluate(panel, model, costs, 100, smokeOnly: false);
        Assert.Equal(2, report.Cells.Length);                       // no symbol holdout: time holdout only
        Assert.Contains(RlPortfolioEvaluationContract.TimeHoldoutOnlyNote, report.Notes);
        Assert.Equal(ContentHash.OfFile(model), report.ModelSha256);
        Assert.Equal(RlShortHistoryEvaluationContract.SpecificationId, report.EvaluationSpecificationId);
        RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, report.ModelSha256, costs, 100);

        // The policy arm recomposed by hand for the first validation block.
        using var actor = new RlPortfolioActorModel(model);
        var cell = report.Cells[0];
        var block = panel.Catalog.CellBlocks(RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Train)[0];
        var tradable = panel.Catalog.EligibleIn(block, RlPanelSymbolGroup.Train);
        using var session = new RlPortfolioSession(panel, block, tradable, costs);
        var snapshot = session.Reset();
        for (int i = 0; i < RlContract.EpisodeHorizon; i++)
        {
            var weights = actor.Decide(RlPortfolioObservationBuilder.Build(panel, 3, snapshot, tradable, actor.Scaler),
                RlPortfolioObservationBuilder.SlotMask(panel.SymbolCount, 3, tradable), 100);
            snapshot = session.Step(weights.Assets.Take(panel.SymbolCount).ToImmutableArray()).Snapshot;
        }
        Assert.Equal((snapshot.Equity - costs.InitialCapital) / costs.InitialCapital, cell.Blocks[0].PolicyReturn);
        Assert.Equal(0m, cell.Blocks[0].CashReturn);
    }

    [Fact]
    public async Task AModelOfAnotherPanelOrSpecificationIsRejected()
    {
        var panel = await Panel();
        string good = Model(panel);
        Assert.Equal(2, RlPortfolioEvaluator.Evaluate(panel, good, Costs(), 100, false).Cells.Length);
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluator.Evaluate(panel,
            Model(panel, m => m[RlPortfolioContract.MetadataUniverseSha256] = new string('0', 64)), Costs(), 100, false));
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluator.Evaluate(panel,
            Model(panel, m => m[RlPortfolioContract.MetadataCalendarSha256] = new string('0', 64)), Costs(), 100, false));
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluator.Evaluate(panel,
            Model(panel, m => m[RlPortfolioContract.MetadataEvaluationSpecification] = RlFiles.EvaluationRevision), Costs(), 100, false));
    }
}
