#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Opt-in real run of the portfolio worker (managed Python venv with the pinned stack required): synthetic wave prices in an isolated Data root,
/// short-history evaluation, smoke-sized training. It proves the plumbing (job, strict dependency capture, Core host, PPO, ONNX export, C# parity,
/// artifact publication); it is not market data, not a trained-quality claim and not a qualification.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlPortfolioTrainingSmokeTests : IDisposable
{
    public const string EnabledVariable = "SA_RL_PORTFOLIO_SMOKE";
    public sealed class SmokeFactAttribute : FactAttribute
    {
        public SmokeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
                Skip = $"Opt-in real portfolio worker smoke run (managed Python venv); set {EnabledVariable}=1.";
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_smoke_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private string DataRoot => Path.Combine(_root, "data");

    public RlPortfolioTrainingSmokeTests()
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

    private void WriteWave(string symbol, int phase, int count)
    {
        string close = $"(100 + 10 * sin(range / 7.0 + {phase}) + range / 40.0)";
        string daily = Path.Combine(DataRoot, "Daily"), meta = Path.Combine(DataRoot, "Metadata");
        Directory.CreateDirectory(daily); Directory.CreateDirectory(meta);
        RlSourceFixture.CreateParquet(Path.Combine(daily, symbol + ".parquet"),
            $"SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date,({close}*(1+0.002*sin(range)))::FLOAT AS open,({close}+2)::FLOAT AS high,({close}-2)::FLOAT AS low,{close}::FLOAT AS close,1000::BIGINT AS volume FROM range({count})");
        RlSourceFixture.CreateParquet(Path.Combine(meta, symbol + ".meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
    }

    private const int Days = 1_400;
    private static readonly string[] Symbols = { "WAVA", "WAVB", "WAVC", "WAVD" };

    /// <summary>Synthetic wave symbols captured into one immutable short-history panel with the holdout on; the same panel can feed several runs.</summary>
    private async Task<(RlPanelSnapshot Panel, RlPortfolioSettings Settings, BacktestConfiguration Costs)> PreparePanelAsync(
        RlPanelPartitionPlan? plan = null, int days = Days, string panelName = "panel")
    {
        for (int i = 0; i < Symbols.Length; i++) WriteWave(Symbols[i], i * 2, days);
        var settings = new RlPortfolioSettings();
        var start = RlSourceFixture.FirstDay;
        var panel = await RlPanelSnapshotFactory.PrepareAsync(Symbols, start, start.AddDays(days), Path.Combine(_root, panelName), RlSourceFixture.Limits, settings, true,
            RlPanelEvaluationMode.ShortHistory, plan);
        var costs = new BacktestConfiguration { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 100_000m,
            SlippageRatio = 0.001m, CommissionFlat = 1m, CommissionPerUnit = 0.005m };
        return (panel, settings, costs);
    }

    /// <summary>
    /// Opt-in determinism check on one machine: the smoke training is run twice on the same panel, costs and settings, and every artifact the
    /// run produces is compared. A difference is a finding about this environment (the deterministic flags are necessary, not proven sufficient),
    /// so the failure lists each differing item instead of stopping at the first. It says nothing about other CPUs, operating systems or library versions.
    /// </summary>
    [SmokeFact]
    public async Task TwoSmokeRunsOnTheSamePanelProduceTheSameModelAndEvidence()
    {
        var (panel, settings, costs) = await PreparePanelAsync();
        var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(_root, "artifacts"));
        var request = new RlPortfolioTrainingRequest(panel.Directory, costs, settings, SmokeOnly: true, Mode: RlPanelEvaluationMode.ShortHistory);
        var first = await runner.RunAsync(request);
        var second = await runner.RunAsync(request);
        Assert.NotEqual(first.Directory, second.Directory);

        var differences = new System.Collections.Generic.List<string>();
        void Compare(string name, object? a, object? b) { if (!Equals(a, b)) differences.Add($"{name}: '{a}' != '{b}'"); }
        Compare("selected seed", first.SelectedSeed, second.SelectedSeed);
        Compare("selected checkpoint", first.SelectedCheckpoint, second.SelectedCheckpoint);
        Compare("onnx model sha256", ContentHash.OfFile(first.ModelPath), ContentHash.OfFile(second.ModelPath));
        Compare("scaler sidecar sha256", ContentHash.OfFile(first.ModelPath + RlFiles.ScalerSuffix), ContentHash.OfFile(second.ModelPath + RlFiles.ScalerSuffix));
        Compare("native state sha256", ContentHash.OfFile(Path.Combine(first.Directory, RlFiles.Native)), ContentHash.OfFile(Path.Combine(second.Directory, RlFiles.Native)));
        Compare("parity file sha256", ContentHash.OfFile(first.ParityPath), ContentHash.OfFile(second.ParityPath));
        Compare("evaluation cell count", first.Evaluation.Cells.Length, second.Evaluation.Cells.Length);
        for (int i = 0; i < Math.Min(first.Evaluation.Cells.Length, second.Evaluation.Cells.Length); i++)
        {
            var a = first.Evaluation.Cells[i]; var b = second.Evaluation.Cells[i];
            Compare($"cell {a.Partition}/{a.Group} policy distribution", a.Policy, b.Policy);
        }
        Assert.True(differences.Count == 0, "Two smoke runs on the same panel differ in " + differences.Count + " item(s):" + Environment.NewLine + string.Join(Environment.NewLine, differences));
    }

    [SmokeFact]
    public async Task SmokeRunTrainsExportsValidatesAndPublishes()
    {
        var symbols = Symbols;
        var (panel, settings, costs) = await PreparePanelAsync();
        string panelDirectory = panel.Directory;

        var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(_root, "artifacts"));
        RlPortfolioTrainingResult result;
        try
        {
            result = await runner.RunAsync(new RlPortfolioTrainingRequest(panelDirectory, costs, settings, SmokeOnly: true,
                Mode: RlPanelEvaluationMode.ShortHistory));
        }
        catch
        {
            // Keep the worker diagnostics of a failed smoke run (the isolated root is deleted afterwards).
            foreach (string log in Directory.EnumerateFiles(Path.Combine(_root, "artifacts"), "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".log") || f.EndsWith("failure.json")))
                File.Copy(log, Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_smoke_" + Path.GetFileName(log)), true);
            throw;
        }

        Assert.Equal(42, result.SelectedSeed);
        Assert.Contains(result.SelectedCheckpoint, new[] { 512, 1024 });
        Assert.True(File.Exists(result.ModelPath) && File.Exists(result.ParityPath) && File.Exists(result.ModelPath + RlFiles.ScalerSuffix));
        using var model = new RlPortfolioActorModel(result.ModelPath);
        Assert.Equal(symbols.Length, model.MaxSymbols);
        model.ValidateParity(result.ParityPath);
        var store = new RlPortfolioArtifactStore(Path.Combine(_root, "artifacts"));
        var entry = Assert.Single(store.List());
        Assert.Equal(RlPortfolioArtifactStatus.Verified, entry.Status);
        Assert.Equal(result.UniverseSha256, entry.Manifest!.UniverseSha256);
        Assert.Contains(RlFiles.Model, entry.Manifest.Files.Keys);
        // The selection evidence is a hashed artifact: the confirmed pair, every complete candidate in selection order, the transitions per seed.
        Assert.Contains("training_summary.json", entry.Manifest.Files.Keys);
        var summary = JsonSerializer.Deserialize<RlPortfolioTrainingSummary>(File.ReadAllText(Path.Combine(result.Directory, "training_summary.json")), RlFiles.Json)!;
        Assert.Equal((result.SelectedSeed, result.SelectedCheckpoint, true), (summary.Selected.Seed, summary.Selected.Checkpoint, summary.SmokeOnly));
        Assert.Equal((summary.Selected.Seed, summary.Selected.Checkpoint, summary.Selected.MeanReturn),
            (summary.Candidates[0].Seed, summary.Candidates[0].Checkpoint, summary.Candidates[0].MeanReturn));
        Assert.NotEmpty(summary.Candidates);
        Assert.Equal(1_024, Assert.Single(summary.SeedProgress).TrainingTransitions);
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, RlFiles.Job)));
        Assert.True(job.RootElement.GetProperty("smoke_only").GetBoolean());
        // A run without a transfer is the run it always was: no transfer keys in the job, no transfer files published.
        Assert.False(job.RootElement.TryGetProperty("transfer", out _) || job.RootElement.TryGetProperty("transfer_defaults", out _));
        Assert.DoesNotContain(entry.Manifest.Files.Keys, name => name is RlPortfolioTransferContract.InitWeightsFile or RlPortfolioTransferContract.LineageFile);

        // The evaluation report is published with the evidence, read back from disk and consistent with the panel, the model and the costs.
        Assert.Contains(RlPortfolioEvaluationContract.ReportFile, entry.Manifest.Files.Keys);
        var report = RlPortfolioEvaluationReports.Read(result.ReportPath);
        RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, ContentHash.OfFile(result.ModelPath),
            RlContract.CreateConfiguration(costs), settings.MaxWeightPercent);
        Assert.Equal(report.Cells.Length, result.Evaluation.Cells.Length);
        Assert.All(report.Cells, cell => Assert.True(cell.Evaluable || cell.Group == RlPanelSymbolGroup.Holdout));
        Assert.True(report.SmokeOnly);
    }

    /// <summary>
    /// Panels below the ratio minimum (the ratio rule needs 1278 bars) train, evaluate and publish under the block-count plan with one train block
    /// (1000 bars) and with three (1100 bars); the partition evidence is published, read back and validated, and so is the evaluation report.
    /// </summary>
    [SmokeFact]
    public async Task SmokeRunUnderABlockCountPlanPublishesAndValidatesThePartitionEvidence()
    {
        foreach (var (name, days, trainBlocks) in new[] { ("one", 1_000, 1), ("three", 1_100, 3) })
        {
            var plan = new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(trainBlocks, 1, 1));
            Assert.True(days < RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory));
            Assert.True(days >= RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, plan));
            var (panel, settings, costs) = await PreparePanelAsync(plan, days, "panel_" + name);
            Assert.Equal(trainBlocks, panel.Catalog.Train.Length);
            var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(_root, "artifacts_" + name));
            var result = await runner.RunAsync(new RlPortfolioTrainingRequest(panel.Directory, costs, settings, SmokeOnly: true,
                Mode: RlPanelEvaluationMode.ShortHistory, Plan: plan));
            var entry = Assert.Single(new RlPortfolioArtifactStore(Path.Combine(_root, "artifacts_" + name)).List());
            Assert.Equal(RlPortfolioArtifactStatus.Verified, entry.Status);
            Assert.Contains(RlPortfolioPartitionEvidence.FileName, entry.Manifest!.Files.Keys);
            var document = RlPortfolioPartitionEvidence.Read(Path.Combine(result.Directory, RlPortfolioPartitionEvidence.FileName));
            var stored = RlPortfolioPartitionEvidence.Validate(document, panel);
            Assert.Equal(plan.Requirement, stored.Requirement);
            var report = RlPortfolioEvaluationReports.Read(result.ReportPath);
            RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, ContentHash.OfFile(result.ModelPath),
                RlContract.CreateConfiguration(costs), settings.MaxWeightPercent, stored);
            Assert.All(report.Cells, cell => Assert.True(cell.Evaluable || cell.Group == RlPanelSymbolGroup.Holdout));
        }
    }

    /// <summary>
    /// A v2-a smoke run (seven channels) trains in the real worker, exports a model of its own contract, evaluates and publishes a report that names it,
    /// and a v1 run of the same panel stays a v1 run: the job of the v1 run has no channel key, the job of the v2-a run carries it.
    /// </summary>
    [SmokeFact]
    public async Task ASmokeRunOfTheV2VariantTrainsExportsAndPublishesUnderItsOwnContract()
    {
        var (panel, settings, costs) = await PreparePanelAsync();
        var layout = RlPortfolioObservationLayout.V2A;
        var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(_root, "artifacts"));
        var v2 = await runner.RunAsync(new RlPortfolioTrainingRequest(panel.Directory, costs, settings, SmokeOnly: true,
            Mode: RlPanelEvaluationMode.ShortHistory, Layout: layout));
        using (var model = new RlPortfolioActorModel(v2.ModelPath))
        {
            Assert.Same(layout, model.Layout);
            Assert.Equal(layout.ObservationLength(model.MaxSymbols), RlPortfolioResources.ObservationLength(model.MaxSymbols, layout));
            model.ValidateParity(v2.ParityPath);
        }
        var scaler = RlPortfolioScaler.Load(v2.ModelPath, new System.Collections.Generic.Dictionary<string, string>
        {
            ["scaler_ref"] = RlPortfolioScaler.SidecarName(v2.ModelPath), ["window_size"] = "64", ["channels"] = "7",
            [RlPortfolioContract.MetadataContractHash] = layout.ContractHash,
        });
        Assert.Equal(7, scaler.Statistics.Length);
        var report = RlPortfolioEvaluationReports.Read(v2.ReportPath);
        Assert.Equal(layout.ContractHash, report.PortfolioContractHash);
        RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, ContentHash.OfFile(v2.ModelPath),
            RlContract.CreateConfiguration(costs), settings.MaxWeightPercent, layout: layout);
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(v2.Directory, RlFiles.Job)));
        Assert.Equal(7, job.RootElement.GetProperty("market_channels").GetInt32());
        Assert.Equal(layout.ContractHash, job.RootElement.GetProperty("contract_hash").GetString());

        var v1 = await runner.RunAsync(new RlPortfolioTrainingRequest(panel.Directory, costs, settings, SmokeOnly: true, Mode: RlPanelEvaluationMode.ShortHistory));
        using var v1Job = JsonDocument.Parse(File.ReadAllText(Path.Combine(v1.Directory, RlFiles.Job)));
        Assert.False(v1Job.RootElement.TryGetProperty("market_channels", out _));
        Assert.Equal(RlPortfolioContract.ContractHash, v1Job.RootElement.GetProperty("contract_hash").GetString());
        Assert.True(RlPortfolioTransferComparison.Compare(report, RlPortfolioEvaluationReports.Read(v1.ReportPath), acrossModelContracts: true).AcrossModelContracts);
    }

    /// <summary>A run-level seed offset shifts the candidate seeds (42 becomes 45 for offset 3), is written to the job only when it is not zero, and makes another run (another model).</summary>
    [SmokeFact]
    public async Task ARunLevelSeedOffsetShiftsTheSeedsAndLeavesTheZeroOffsetRunUntouched()
    {
        var (panel, settings, costs) = await PreparePanelAsync();
        var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(_root, "artifacts"));
        RlPortfolioTrainingRequest Request(int offset) => new(panel.Directory, costs, settings, SmokeOnly: true, Mode: RlPanelEvaluationMode.ShortHistory, SeedOffset: offset);
        var zero = await runner.RunAsync(Request(0));
        var shifted = await runner.RunAsync(Request(RlPortfolioAblationContract.SeedOffset(1)));
        Assert.Equal(42, zero.SelectedSeed);
        Assert.Equal(42 + RlPortfolioAblationContract.SeedOffset(1), shifted.SelectedSeed);
        Assert.NotEqual(ContentHash.OfFile(zero.ModelPath), ContentHash.OfFile(shifted.ModelPath));
        using var zeroJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(zero.Directory, RlFiles.Job)));
        using var shiftedJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(shifted.Directory, RlFiles.Job)));
        Assert.False(zeroJob.RootElement.TryGetProperty("seed_offset", out _));
        Assert.Equal(RlPortfolioAblationContract.SeedOffset(1), shiftedJob.RootElement.GetProperty("seed_offset").GetInt32());
    }

    /// <summary>A default run publishes exactly the files it published before: no partition evidence for a null plan or the default plan.</summary>
    [SmokeFact]
    public async Task ADefaultPlanRunPublishesNoPartitionEvidence()
    {
        var (panel, settings, costs) = await PreparePanelAsync();
        var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(_root, "artifacts"));
        var result = await runner.RunAsync(new RlPortfolioTrainingRequest(panel.Directory, costs, settings, SmokeOnly: true,
            Mode: RlPanelEvaluationMode.ShortHistory, Plan: RlPanelPartitionPlan.FromSettings(settings)));
        var entry = Assert.Single(new RlPortfolioArtifactStore(Path.Combine(_root, "artifacts")).List());
        Assert.DoesNotContain(RlPortfolioPartitionEvidence.FileName, entry.Manifest!.Files.Keys);
        Assert.False(File.Exists(Path.Combine(result.Directory, RlPortfolioPartitionEvidence.FileName)));
    }
}
