#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
/// Stored-model evaluation on a new panel: base-run listing and admission (every rule in its order), the lineage file, the evaluator entry point that skips
/// the identity checks, and the service end to end. Panels are real snapshots of synthetic wave prices in an isolated Data root; the base "model" is the
/// linear three-slot ONNX fixture (not trained) published into a real artifact store folder, so the store, the manifests and the hashes are the real ones.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlPortfolioTransferTests : IDisposable
{
    private static readonly DateOnly First = RlSourceFixture.FirstDay;
    private static readonly string[] Three = { "WAVA", "WAVB", "WAVC" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_transfer_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private readonly RlPortfolioSettings _settings = new();
    private string DataRoot => Path.Combine(_root, "data");
    private RlPortfolioArtifactStore Store => new(DataRoot);

    public RlPortfolioTransferTests()
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

    private void WriteWave(string symbol, int phase, int count)
    {
        string close = $"(100 + 10 * sin(range / 7.0 + {phase}) + range / 40.0)";
        string daily = Path.Combine(DataRoot, "Daily"), meta = Path.Combine(DataRoot, "Metadata");
        Directory.CreateDirectory(daily); Directory.CreateDirectory(meta);
        RlSourceFixture.CreateParquet(Path.Combine(daily, symbol + ".parquet"),
            $"SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date,({close}*(1+0.002*sin(range)))::FLOAT AS open,({close}+2)::FLOAT AS high,({close}-2)::FLOAT AS low,{close}::FLOAT AS close,1000::BIGINT AS volume FROM range({count})");
        RlSourceFixture.CreateParquet(Path.Combine(meta, symbol + ".meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
    }

    private void WriteWaves(int count, params string[] symbols) { for (int i = 0; i < symbols.Length; i++) WriteWave(symbols[i], i * 2, count); }

    private Task<RlPanelSnapshot> Panel(string[] symbols, int days, RlPanelPartitionPlan? plan = null, int startOffset = 0, bool holdout = false)
        => RlPanelSnapshotFactory.PrepareAsync(symbols, First.AddDays(startOffset), First.AddDays(days), Store.PanelDirectory(Guid.NewGuid()), RlSourceFixture.Limits, _settings,
            holdoutEnabled: holdout, RlPanelEvaluationMode.ShortHistory, plan);

    /// <summary>Writes the fixture actor with the identity of <paramref name="panel"/> into <paramref name="directory"/> (model and scaler sidecar).</summary>
    private static string WriteModel(RlPanelSnapshot panel, string directory, Action<Dictionary<string, string>>? change = null, RlPortfolioObservationLayout? layout = null)
    {
        layout ??= RlPortfolioObservationLayout.V1;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, RlFiles.Model);
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
            ["scaler_ref"] = RlFiles.Model + RlFiles.ScalerSuffix,
        };
        change?.Invoke(metadata);
        string fixture = layout == RlPortfolioObservationLayout.V1 ? "rl_portfolio_fixture_n3.onnx" : "rl_portfolio_fixture_v2a_n3.onnx";
        OnnxMetadataFixture.Write(Path.Combine(AppContext.BaseDirectory, "Assets", fixture), path, metadata);
        RlPortfolioScaler.Save(path, RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(0d, 0.05d, -1d, 1d), layout.MarketChannelCount).ToArray(), layout));
        return path;
    }

    /// <summary>Publishes a base run for <paramref name="panel"/> into the store; the three model files are listed unless one is left out.</summary>
    private async Task<Guid> PublishBase(RlPanelSnapshot panel, bool native = true, bool publish = true, Action<Dictionary<string, string>>? change = null,
        RlPortfolioObservationLayout? layout = null)
    {
        var store = Store;
        var runId = Guid.NewGuid();
        string directory = store.Begin(runId);
        WriteModel(panel, directory, change, layout);
        File.WriteAllText(Path.Combine(directory, RlFiles.Native), "native weights stand-in");
        File.WriteAllText(Path.Combine(directory, "extra.txt"), "evidence");
        if (publish)
        {
            var names = new List<string> { RlFiles.Model, RlFiles.Model + RlFiles.ScalerSuffix, "extra.txt" };
            if (native) names.Add(RlFiles.Native);
            await store.PublishAsync(runId, panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256, names);
        }
        return runId;
    }

    /// <summary>
    /// A hand-written copy of a panel manifest with another period end. A real capture can never produce it (the period is part of the source manifests
    /// that the universe hash covers), so this is the only way to meet the "hashes agree but periods differ" case the reader must still refuse.
    /// </summary>
    private void ForgePanelWithAnotherPeriod(RlPanelSnapshot panel, int extraDays)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(panel.Directory, RlPortfolioContract.PanelManifest)))!;
        node["period_end_exclusive"] = panel.Manifest.PeriodEndExclusive.AddDays(extraDays).ToString("yyyy-MM-dd");
        string folder = Store.PanelDirectory(Guid.NewGuid());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RlPortfolioContract.PanelManifest), node.ToJsonString());
    }

    private static DateOnly FirstValidation(RlPanelSnapshot panel) => RlPortfolioTransferAdmission.FirstValidationDate(panel);

    /// <summary>The panel folders in the store, sorted, so a test can prove that a rejected attempt left exactly the earlier ones.</summary>
    private string[] PanelFolders()
        => Directory.Exists(Store.PanelsRoot) ? Directory.GetDirectories(Store.PanelsRoot).OrderBy(d => d, StringComparer.Ordinal).ToArray() : Array.Empty<string>();

    private static void AssertRejected(string detail, Action act)
    {
        var error = Assert.Throws<InvalidDataException>(act);
        Assert.Equal($"{RlPortfolioTransferContract.ErrorPrefix} {detail}", error.Message);
    }

    // ----- the contract -----

    [Fact]
    public void TheTransferContractHashCoversEveryPublicConstantAndNothingElseOfThePinnedContracts()
    {
        var constants = typeof(RlPortfolioTransferContract).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => f.Name).ToArray();
        var wire = RlPortfolioTransferContract.ToWireDefaults();
        Assert.Equal(constants.Append("NumericalContractHash").OrderBy(n => n, StringComparer.Ordinal), wire.Keys.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(wire)))), RlPortfolioTransferContract.ContractHash);
        Assert.Equal(RlPortfolioContract.ContractHash, wire["NumericalContractHash"]);
        Assert.Equal("69833CFB09187763C1028E7008A7C04DDE3BB69B217B78389E568A090765B6C6", RlPortfolioTransferContract.ContractHash);      // pinned: a changed constant invalidates every stored lineage
        Assert.Equal(("SA-RL-PORTFOLIO-TRANSFER-v1", "transfer.json", "init_weights.pt", "fine_tune", "zero_shot_evaluation", "encoder", "none"),
            (RlPortfolioTransferContract.SpecificationId, RlPortfolioTransferContract.LineageFile, RlPortfolioTransferContract.InitWeightsFile,
                RlPortfolioTransferContract.FineTuneKind, RlPortfolioTransferContract.ZeroShotKind, RlPortfolioTransferContract.EncoderFrozen, RlPortfolioTransferContract.NoFreeze));
        // The transfer constants never reach the pinned training and evaluation contracts.
        Assert.DoesNotContain(RlPortfolioContract.ToWireDefaults().Keys, k => k.Contains("Transfer", StringComparison.Ordinal) || k == nameof(RlPortfolioTransferContract.Statement));
        Assert.DoesNotContain(RlPortfolioEvaluationContract.ToWireDefaults().Keys, k => k == nameof(RlPortfolioTransferContract.ErrorPrefix));
    }

    // ----- listing -----

    [Fact]
    public async Task OnlyVerifiedRunsWithAllThreeModelFilesAreListedWithTheirDataPeriod()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var good = await PublishBase(panel);
        await PublishBase(panel, native: false);                 // lacks the native weights
        await PublishBase(panel, publish: false);                // never published: Incomplete
        var infos = RlPortfolioBaseRuns.List(Store);
        var info = Assert.Single(infos);
        Assert.Equal(good, info.RunId);
        Assert.Equal((panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256), (info.UniverseSha256, info.CalendarSha256));
        Assert.Equal(new RlBasePeriod(RlBasePeriodStatus.Known, First, First.AddDays(1_444)), info.Period);
        Assert.Empty(RlPortfolioBaseRuns.List(new RlPortfolioArtifactStore(Path.Combine(_root, "elsewhere"))));
    }

    [Fact]
    public async Task APeriodIsUnknownWithoutAStoredPanelAndAmbiguousWhenStoredManifestsDisagree()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        await PublishBase(panel);
        Assert.Equal(RlBasePeriodStatus.Known, Assert.Single(RlPortfolioBaseRuns.List(Store)).Period.Status);
        ForgePanelWithAnotherPeriod(panel, 56);                  // same universe and calendar hashes, another period end
        Assert.Equal(RlBasePeriodStatus.Ambiguous, Assert.Single(RlPortfolioBaseRuns.List(Store)).Period.Status);
        Directory.Delete(Store.PanelsRoot, recursive: true);
        Assert.Equal(RlBasePeriodStatus.Unknown, Assert.Single(RlPortfolioBaseRuns.List(Store)).Period.Status);
    }

    [Fact]
    public async Task AnUnparsablePanelFolderIsSkippedNotRepaired()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        await PublishBase(panel);
        string junk = Store.PanelDirectory(Guid.NewGuid());
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, RlPortfolioContract.PanelManifest), "{ not json");
        Directory.CreateDirectory(Store.PanelDirectory(Guid.NewGuid()));      // no manifest at all
        Assert.Equal(RlBasePeriodStatus.Known, Assert.Single(RlPortfolioBaseRuns.List(Store)).Period.Status);
        Assert.True(File.Exists(Path.Combine(junk, RlPortfolioContract.PanelManifest)));
    }

    // ----- one run by identity (W2) -----

    private static void AssertSameEntry(RlPortfolioArtifactEntry expected, RlPortfolioArtifactEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.RunId, expected.Directory, expected.Status, expected.Problem), (actual!.RunId, actual.Directory, actual.Status, actual.Problem));
        Assert.Equal(expected.Manifest?.Files, actual.Manifest?.Files);
        Assert.Equal(expected.Manifest?.CreatedUtc, actual.Manifest?.CreatedUtc);
        Assert.Equal((expected.Manifest?.UniverseSha256, expected.Manifest?.CalendarSha256), (actual.Manifest?.UniverseSha256, actual.Manifest?.CalendarSha256));
    }

    [Fact]
    public async Task FindReturnsTheSameEntryAsTheListingForEveryStatusAndNullForAnUnknownRun()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var verified = await PublishBase(panel);
        var incomplete = await PublishBase(panel, publish: false);
        var corrupt = await PublishBase(panel);
        File.AppendAllText(Path.Combine(Store.Root, corrupt.ToString("N"), "extra.txt"), "tampered");
        var invalid = await PublishBase(panel);
        File.WriteAllText(Path.Combine(Store.Root, invalid.ToString("N"), RlPortfolioContract.ArtifactManifest), "{ not json");
        var listed = Store.List();
        Assert.Equal(new[] { RlPortfolioArtifactStatus.Verified, RlPortfolioArtifactStatus.Incomplete, RlPortfolioArtifactStatus.Corrupt, RlPortfolioArtifactStatus.Invalid },
            new[] { verified, incomplete, corrupt, invalid }.Select(id => listed.Single(e => e.RunId == id).Status));
        foreach (var id in new[] { verified, incomplete, corrupt, invalid }) AssertSameEntry(listed.Single(e => e.RunId == id), Store.Find(id));
        Assert.Null(Store.Find(Guid.NewGuid()));
        Assert.Null(Store.Find(Guid.Empty));                                                  // RunDirectory would throw for it; admission must keep rejecting
        Assert.Null(new RlPortfolioArtifactStore(Path.Combine(_root, "elsewhere")).Find(verified));
    }

    [Fact]
    public async Task FindIsNotCachedAndSeesTheCurrentDiskState()
    {
        WriteWaves(2_400, Three);
        var id = await PublishBase(await Panel(Three, 1_444));
        Assert.Equal(RlPortfolioArtifactStatus.Verified, Store.Find(id)!.Status);
        File.AppendAllText(Path.Combine(Store.Root, id.ToString("N"), "extra.txt"), "tampered");
        Assert.Equal(RlPortfolioArtifactStatus.Corrupt, Store.Find(id)!.Status);
    }

    [Fact]
    public async Task AnotherRunWithACorruptFileDoesNotChangeTheAdmissionOfAGoodBase()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var good = await PublishBase(panel);
        var before = RlPortfolioTransferAdmission.Admit(Store, good, First.AddDays(1_444));
        var other = await PublishBase(panel);
        File.AppendAllText(Path.Combine(Store.Root, other.ToString("N"), "extra.txt"), "tampered");
        var after = RlPortfolioTransferAdmission.Admit(Store, good, First.AddDays(1_444));
        Assert.Equal((before.RunId, before.ModelSha256, before.ScalerSha256, before.NativeSha256, before.MaxSymbols, before.PeriodStartInclusive, before.PeriodEndExclusive),
            (after.RunId, after.ModelSha256, after.ScalerSha256, after.NativeSha256, after.MaxSymbols, after.PeriodStartInclusive, after.PeriodEndExclusive));
        AssertRejected("the base run is not a verified run.", () => RlPortfolioTransferAdmission.Admit(Store, other, First.AddDays(2_000)));
        AssertRejected("the base run is not a verified run.", () => RlPortfolioTransferAdmission.Admit(Store, Guid.Empty, First.AddDays(2_000)));
    }

    // ----- admission: every rule, in its order -----

    /// <summary>A fine-tune keeps the encoder input of its base: a base of another observation variant is rejected for the target, whichever way round.</summary>
    [Fact]
    public async Task AdmissionRejectsABaseOfAnotherObservationVariantAndAdmitsTheSameVariant()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var v1 = await PublishBase(panel);
        var v2 = await PublishBase(panel, layout: RlPortfolioObservationLayout.V2A);
        var when = First.AddDays(1_444);
        Assert.Equal(v1, RlPortfolioTransferAdmission.Admit(Store, v1, when).RunId);
        Assert.Equal(v1, RlPortfolioTransferAdmission.Admit(Store, v1, when, RlPortfolioObservationLayout.V1).RunId);
        Assert.Equal(v2, RlPortfolioTransferAdmission.Admit(Store, v2, when, RlPortfolioObservationLayout.V2A).RunId);
        AssertRejected("the base run belongs to a different portfolio contract.", () => RlPortfolioTransferAdmission.Admit(Store, v2, when));
        AssertRejected("the base run belongs to a different portfolio contract.", () => RlPortfolioTransferAdmission.Admit(Store, v1, when, RlPortfolioObservationLayout.V2A));
    }

    [Fact]
    public async Task AdmissionReturnsTheBaseRunWithItsIdentitiesWhenEveryRuleHolds()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var runId = await PublishBase(panel);
        var admitted = RlPortfolioTransferAdmission.Admit(Store, runId, First.AddDays(1_444));
        var entry = Store.List().Single(e => e.RunId == runId);
        Assert.Equal((runId, 3, First, First.AddDays(1_444)), (admitted.RunId, admitted.MaxSymbols, admitted.PeriodStartInclusive, admitted.PeriodEndExclusive));
        Assert.Equal(entry.Manifest!.Files[RlFiles.Model], admitted.ModelSha256);
        Assert.Equal(entry.Manifest.Files[RlFiles.Model + RlFiles.ScalerSuffix], admitted.ScalerSha256);
        Assert.Equal(entry.Manifest.Files[RlFiles.Native], admitted.NativeSha256);
        Assert.Equal((panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256), (admitted.UniverseSha256, admitted.CalendarSha256));
        Assert.True(File.Exists(admitted.ModelPath) && File.Exists(admitted.ScalerPath) && File.Exists(admitted.NativePath));
    }

    [Fact]
    public async Task Rule1ARunThatIsNotVerifiedIsRejected()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var incomplete = await PublishBase(panel, publish: false);
        var corrupt = await PublishBase(panel);
        File.AppendAllText(Path.Combine(Store.Root, corrupt.ToString("N"), "extra.txt"), "tampered");
        Assert.Equal(RlPortfolioArtifactStatus.Corrupt, Store.List().Single(e => e.RunId == corrupt).Status);
        var invalid = await PublishBase(panel);
        File.WriteAllText(Path.Combine(Store.Root, invalid.ToString("N"), RlPortfolioContract.ArtifactManifest), "{ not json");
        foreach (var id in new[] { Guid.NewGuid(), incomplete, corrupt, invalid })
            AssertRejected("the base run is not a verified run.", () => RlPortfolioTransferAdmission.Admit(Store, id, First.AddDays(2_000)));
    }

    [Fact]
    public async Task Rule2ARunWithoutTheNativeWeightsIsRejectedByName()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var id = await PublishBase(panel, native: false);
        AssertRejected("the base run lacks policy.pt.", () => RlPortfolioTransferAdmission.Admit(Store, id, First.AddDays(2_000)));
    }

    [Fact]
    public async Task Rule3AModelOfAnotherContractIsRejected()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var wrongHash = await PublishBase(panel, change: m => m[RlPortfolioContract.MetadataContractHash] = new string('0', 64));
        var wrongTarget = await PublishBase(panel, change: m => m[RlPortfolioContract.MetadataTargetType] = "other");
        foreach (var id in new[] { wrongHash, wrongTarget })
            AssertRejected("the base run belongs to a different portfolio contract.", () => RlPortfolioTransferAdmission.Admit(Store, id, First.AddDays(2_000)));
    }

    [Fact]
    public async Task Rule4AnUnknownOrAmbiguousBasePanelIsRejected()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var id = await PublishBase(panel);
        ForgePanelWithAnotherPeriod(panel, 56);
        AssertRejected("the base panel period is ambiguous.", () => RlPortfolioTransferAdmission.Admit(Store, id, First.AddDays(2_000)));
        Directory.Delete(Store.PanelsRoot, recursive: true);
        AssertRejected("the base panel is unknown, its period cannot be proven.", () => RlPortfolioTransferAdmission.Admit(Store, id, First.AddDays(2_000)));
    }

    [Fact]
    public async Task Rule5TheBaseDataMustEndNoLaterThanTheFirstValidationDate()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var id = await PublishBase(panel);
        var equal = First.AddDays(1_444);
        Assert.Equal(equal, RlPortfolioTransferAdmission.Admit(Store, id, equal).PeriodEndExclusive);              // equality passes (bars below the exclusive end only)
        Assert.Equal(equal, RlPortfolioTransferAdmission.Admit(Store, id, equal.AddDays(1)).PeriodEndExclusive);   // a later validation start passes too
        AssertRejected($"the base data reaches {equal:yyyy-MM-dd}, the target validation starts {equal.AddDays(-1):yyyy-MM-dd}.",
            () => RlPortfolioTransferAdmission.Admit(Store, id, equal.AddDays(-1)));
    }

    [Fact]
    public async Task TheFirstValidationDateIsTheCalendarDateOneWindowAfterTheTrainCut()
    {
        WriteWaves(2_400, Three);
        var ratio = await Panel(Three, 2_300);                    // cut1 = 1380, validation starts at index 1444
        Assert.Equal(First.AddDays(1_444), FirstValidation(ratio));
        Assert.Equal(ratio.Catalog.Validation[0].StartIndex, ratio.Catalog.TrainCut + RlContract.WindowLength);
        var block = await Panel(Three, 2_300, new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(1, 1, 1)));
        Assert.Equal(block.Catalog.Validation[0].StartIndex, block.Catalog.TrainCut + RlContract.WindowLength);
        Assert.Equal(First.AddDays(block.Catalog.Validation[0].StartIndex), FirstValidation(block));
    }

    // ----- the lineage -----

    private async Task<(RlPortfolioBaseRun Base, DateOnly FirstValidation)> Admitted()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var id = await PublishBase(panel);
        var first = First.AddDays(1_500);
        return (RlPortfolioTransferAdmission.Admit(Store, id, first), first);
    }

    [Fact]
    public async Task TheLineageRoundTripsAndValidatesForBothKinds()
    {
        var (baseRun, first) = await Admitted();
        var zero = RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.ZeroShotKind, baseRun, first);
        var tuned = RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.FineTuneKind, baseRun, first, RlPortfolioTransferContract.EncoderFrozen);
        Assert.Equal((1, "SA-RL-PORTFOLIO-TRANSFER-v1", null, RlPortfolioTransferContract.ContractHash, RlPortfolioTransferContract.Statement),
            (zero.Schema, zero.SpecificationId, zero.Freeze, zero.ContractHash, zero.Statement));
        Assert.Equal(("encoder", baseRun.RunId, First.AddDays(1_444), first), (tuned.Freeze, tuned.BaseRunId, tuned.BasePeriodEndExclusive, tuned.FirstValidationDate));
        string path = Path.Combine(_root, RlPortfolioTransferContract.LineageFile);
        await RlPortfolioTransferEvidence.SaveAsync(path, zero);
        var read = RlPortfolioTransferEvidence.Read(path);
        Assert.Equal(zero, read);
        RlPortfolioTransferEvidence.Validate(read, baseRun, first);
        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(new[] { "base_calendar_sha256", "base_model_sha256", "base_native_sha256", "base_period_end_exclusive", "base_run_id", "base_scaler_sha256",
            "base_universe_sha256", "contract_hash", "first_validation_date", "freeze", "kind", "schema", "specification_id", "statement" },
            json.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    public static IEnumerable<object[]> Tamperings()
    {
        yield return new object[] { "schema", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { Schema = 2 }), "has an unsupported schema version" };
        yield return new object[] { "specification", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { SpecificationId = "x" }), "belongs to a different specification" };
        yield return new object[] { "contract hash", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { ContractHash = new string('0', 64) }), "was written under a different transfer contract" };
        yield return new object[] { "statement", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { Statement = "nothing to see" }), "does not carry the mandatory limitation statement" };
        yield return new object[] { "kind", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { Kind = "other" }), "names an unknown kind" };
        yield return new object[] { "freeze on zero shot", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { Freeze = "encoder" }), "states a freeze that does not fit its kind" };
        yield return new object[] { "base run", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { BaseRunId = Guid.NewGuid() }), "names a different base run" };
        yield return new object[] { "model", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { BaseModelSha256 = new string('1', 64) }), "names different base files" };
        yield return new object[] { "native", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { BaseNativeSha256 = new string('1', 64) }), "names different base files" };
        yield return new object[] { "scaler", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { BaseScalerSha256 = new string('1', 64) }), "names different base files" };
        yield return new object[] { "universe", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { BaseUniverseSha256 = new string('1', 64) }), "names a different base panel" };
        yield return new object[] { "calendar", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { BaseCalendarSha256 = new string('1', 64) }), "names a different base panel" };
        yield return new object[] { "period end", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { BasePeriodEndExclusive = d.BasePeriodEndExclusive.AddDays(-1) }), "states a different base period end" };
        yield return new object[] { "first validation", (Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument>)(d => d with { FirstValidationDate = d.FirstValidationDate.AddDays(1) }), "states a different first validation date" };
    }

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task ATamperedLineageIsRejected(string name, Func<RlPortfolioTransferDocument, RlPortfolioTransferDocument> tamper, string detail)
    {
        var (baseRun, first) = await Admitted();
        var document = RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.ZeroShotKind, baseRun, first);
        var error = Assert.Throws<InvalidDataException>(() => RlPortfolioTransferEvidence.Validate(tamper(document), baseRun, first));
        Assert.Equal($"{RlPortfolioTransferContract.ErrorPrefix} the lineage {detail}.", error.Message);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public async Task ALineageNeedsAFreezeForAFineTuneAndBreaksTheTimeRuleNowhere()
    {
        var (baseRun, first) = await Admitted();
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.FineTuneKind, baseRun, first));
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.FineTuneKind, baseRun, first, "all"));
        Assert.Equal("none", RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.FineTuneKind, baseRun, first, RlPortfolioTransferContract.NoFreeze).Freeze);
        // A first validation date before the end of the base data is not a lineage the protocol can write.
        var early = baseRun.PeriodEndExclusive.AddDays(-1);
        var error = Assert.Throws<InvalidDataException>(() => RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.ZeroShotKind, baseRun, early));
        Assert.Contains("breaks the time rule", error.Message);
        string path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferEvidence.Read(path));
        File.WriteAllText(path, "null");
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferEvidence.Read(path));
    }

    // ----- the evaluator entry point -----

    [Fact]
    public async Task TheTransferredEvaluationEqualsTheOrdinaryOneOnTheModelsOwnPanel()
    {
        WriteWaves(1_400, Three);
        var panel = await Panel(Three, 1_400);
        string model = WriteModel(panel, Path.Combine(_root, "own"));
        var ordinary = RlPortfolioEvaluator.Evaluate(panel, model, Costs(), 100, smokeOnly: false);
        var transferred = RlPortfolioEvaluator.EvaluateTransferred(panel, model, Costs(), 100, smokeOnly: false);
        Assert.Equal(JsonSerializer.Serialize(ordinary, RlFiles.Json), JsonSerializer.Serialize(transferred, RlFiles.Json));
    }

    [Fact]
    public async Task TheTransferredEvaluationScoresAModelOnAPanelItWasNotTrainedOn()
    {
        WriteWaves(2_400, Three);
        var basePanel = await Panel(Three, 1_444);
        var target = await Panel(Three, 2_300);
        string model = WriteModel(basePanel, Path.Combine(_root, "foreign"));
        // The ordinary entry point refuses the foreign model (its universe/calendar hashes belong to the other panel) ...
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluator.Evaluate(target, model, Costs(), 100, false));
        // ... the transferred one scores it and the report validates against the target panel and the model file.
        var report = RlPortfolioEvaluator.EvaluateTransferred(target, model, Costs(), 100, smokeOnly: false);
        Assert.Equal(ContentHash.OfFile(model), report.ModelSha256);
        Assert.Equal(target.Manifest.UniverseSha256, report.UniverseSha256);
        Assert.Equal(target.Manifest.CalendarSha256, report.CalendarSha256);
        RlPortfolioEvaluationReports.Validate(report, target.Manifest, RlPanelEvaluationMode.ShortHistory, report.ModelSha256, Costs(), 100);
        Assert.NotEqual(basePanel.Manifest.CalendarSha256, report.CalendarSha256);
    }

    [Fact]
    public async Task ThePanelNeedsNoMoreSymbolsThanTheModelHasSlots()
    {
        WriteWaves(1_400, "WAVA", "WAVB", "WAVC", "WAVD");
        var four = await Panel(new[] { "WAVA", "WAVB", "WAVC", "WAVD" }, 1_400);
        string model = WriteModel(four, Path.Combine(_root, "slots"));                 // three slots
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluator.EvaluateTransferred(four, model, Costs(), 100, false));
        var two = await Panel(new[] { "WAVA", "WAVB" }, 1_400);                           // fewer symbols than slots: padded slots are masked
        Assert.Equal(2, RlPortfolioEvaluator.EvaluateTransferred(two, model, Costs(), 100, false).Cells.Length);
    }

    // ----- the service end to end -----

    private RlPortfolioTransferService Service() => new(_settings, dataRoot: DataRoot);

    private static RlPortfolioRunRequest Request(string[] symbols, int days, RlPanelPartitionPlan? plan = null)
        => new(symbols.ToImmutableArray(), First, First.AddDays(days), false, RlPanelEvaluationMode.ShortHistory, Costs(), false, plan);

    [Fact]
    public async Task AStoredModelIsEvaluatedOnANewPanelAndTheEvidenceIsPublished()
    {
        WriteWaves(2_400, Three);
        var basePanel = await Panel(Three, 1_444);
        var baseId = await PublishBase(basePanel);
        var service = Service();
        Assert.Equal(baseId, Assert.Single(service.ListBaseRuns()).RunId);
        var result = await service.EvaluateStoredModelAsync(baseId, Request(Three, 2_300), CancellationToken.None);   // validation starts exactly where the base data ends

        var entries = Store.List();
        Assert.Equal(2, entries.Length);
        Assert.Equal(2, PanelFolders().Length);                                           // an accepted run keeps its panel (the base panel and its own)
        Assert.True(Directory.Exists(result.PanelDirectory));
        var entry = entries.Single(e => e.RunId == result.RunId);
        Assert.Equal(RlPortfolioArtifactStatus.Verified, entry.Status);
        Assert.Equal(new[] { RlPortfolioEvaluationContract.ReportFile, RlPortfolioTransferContract.LineageFile }, entry.Manifest!.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(entry.Manifest.Files.Keys, k => k.StartsWith("policy", StringComparison.Ordinal));      // no model files in an evaluation run
        var baseEntry = entries.Single(e => e.RunId == baseId);
        Assert.Equal(baseEntry.Manifest!.Files[RlFiles.Model], result.Evaluation.ModelSha256);
        var target = RlPanelSnapshot.Load(result.PanelDirectory, RlSourceFixture.Limits, _settings, RlPanelEvaluationMode.ShortHistory);
        RlPortfolioEvaluationReports.Validate(RlPortfolioEvaluationReports.Read(result.ReportPath), target.Manifest, RlPanelEvaluationMode.ShortHistory,
            baseEntry.Manifest.Files[RlFiles.Model], Costs(), _settings.MaxWeightPercent);
        var lineage = RlPortfolioTransferEvidence.Read(Path.Combine(result.Directory, RlPortfolioTransferContract.LineageFile));
        RlPortfolioTransferEvidence.Validate(lineage, result.Base, FirstValidation(target));
        Assert.Equal((RlPortfolioTransferContract.ZeroShotKind, null), (lineage.Kind, lineage.Freeze));
        Assert.False(File.Exists(Path.Combine(result.Directory, RlPortfolioPartitionEvidence.FileName)));              // a default plan writes no partition file
        // The new run is not a base candidate (it holds no model files) and the base run is untouched.
        Assert.Equal(baseId, Assert.Single(service.ListBaseRuns()).RunId);
    }

    [Fact]
    public async Task ANonDefaultPlanPublishesThePartitionEvidenceToo()
    {
        WriteWaves(2_400, Three);
        var basePanel = await Panel(Three, 1_444);
        var baseId = await PublishBase(basePanel);
        var plan = new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(1, 1, 1));
        var result = await Service().EvaluateStoredModelAsync(baseId, Request(Three, 1_900, plan), CancellationToken.None);
        var entry = Store.List().Single(e => e.RunId == result.RunId);
        Assert.Equal(RlPortfolioArtifactStatus.Verified, entry.Status);
        Assert.Contains(RlPortfolioPartitionEvidence.FileName, entry.Manifest!.Files.Keys);
        var target = RlPanelSnapshot.Load(result.PanelDirectory, RlSourceFixture.Limits, _settings, RlPanelEvaluationMode.ShortHistory, plan);
        RlPortfolioPartitionEvidence.Validate(RlPortfolioPartitionEvidence.Read(Path.Combine(result.Directory, RlPortfolioPartitionEvidence.FileName)), target);
        RlPortfolioEvaluationReports.Validate(RlPortfolioEvaluationReports.Read(result.ReportPath), target.Manifest, RlPanelEvaluationMode.ShortHistory,
            result.Evaluation.ModelSha256, Costs(), _settings.MaxWeightPercent, plan);
    }

    [Fact]
    public async Task ABaseThatReachesTheTargetValidationIsRejectedBeforeAnyRunFolderExists()
    {
        WriteWaves(2_400, Three);
        var basePanel = await Panel(Three, 1_445);                                       // one day too long for a 2,300-bar target
        var baseId = await PublishBase(basePanel);
        int runs = Store.List().Length;
        var panels = PanelFolders();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service().EvaluateStoredModelAsync(baseId, Request(Three, 2_300), CancellationToken.None));
        Assert.Equal($"{RlPortfolioTransferContract.ErrorPrefix} the base data reaches {First.AddDays(1_445):yyyy-MM-dd}, the target validation starts {First.AddDays(1_444):yyyy-MM-dd}.", error.Message);
        Assert.True(RlPortfolioTransferRejection.Is(error));
        Assert.Equal(runs, Store.List().Length);
        Assert.Equal(panels, PanelFolders());                                             // the panel this call captured is gone, the earlier ones stay
    }

    [Fact]
    public async Task MoreTargetSymbolsThanBaseSlotsAreRejectedAfterTheCaptureAndBeforeTheEvaluation()
    {
        WriteWaves(2_400, "WAVA", "WAVB", "WAVC", "WAVD");
        var basePanel = await Panel(Three, 1_444);
        var baseId = await PublishBase(basePanel);
        int runs = Store.List().Length;
        var panels = PanelFolders();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Service().EvaluateStoredModelAsync(baseId, Request(new[] { "WAVA", "WAVB", "WAVC", "WAVD" }, 2_300), CancellationToken.None));
        Assert.Equal($"{RlPortfolioTransferContract.ErrorPrefix} the base model has 3 slots for 4 symbols.", error.Message);
        Assert.True(RlPortfolioTransferRejection.Is(error));
        Assert.Equal(runs, Store.List().Length);
        Assert.Equal(panels, PanelFolders());
    }

    [Fact]
    public async Task AUnknownBaseRunIsRejectedAndAPlanOnTheStandardModeIsRefused()
    {
        WriteWaves(2_400, Three);
        var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.EvaluateStoredModelAsync(Guid.NewGuid(), Request(Three, 2_300), CancellationToken.None));
        Assert.Empty(PanelFolders());                                                     // the rejected attempt left no panel behind
        var standard = Request(Three, 2_300, new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(1, 1, 1))) with { Mode = RlPanelEvaluationMode.Standard };
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvaluateStoredModelAsync(Guid.NewGuid(), standard, CancellationToken.None));
    }

    // ----- the dialog's admission date -----

    /// <summary>The data check hands the dialog the date a base run is admitted against; it must be the date of the panel the run would capture, for either method.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThePreflightFirstValidationDateIsTheDateTheCapturedPanelAdmitsWith(bool blockCount)
    {
        WriteWaves(2_400, Three);
        RlPanelPartitionPlan? plan = blockCount ? new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(2, 1, 1)) : null;
        int days = blockCount ? 1_100 : 2_300;
        var preflight = await RlPanelPreflightEvaluator.EvaluateAsync(Three, First, First.AddDays(days), false, RlPanelEvaluationMode.ShortHistory, _settings, RlSourceFixture.Limits, plan: plan);
        var panel = await Panel(Three, days, plan);
        Assert.True(preflight.Feasible);
        Assert.Equal(FirstValidation(panel), preflight.FirstValidationDate);
        Assert.Equal(RlPortfolioTransferAdmission.FirstValidationDate(panel.Catalog, panel.Calendar), preflight.FirstValidationDate);
    }

    [Fact]
    public async Task ThePreflightHasNoValidationDateWhenNoSymbolIsUsable()
    {
        WriteWaves(300, Three);
        var none = await RlPanelPreflightEvaluator.EvaluateAsync(new[] { "NOPE" }, First, First.AddDays(250), false, RlPanelEvaluationMode.ShortHistory, _settings, RlSourceFixture.Limits);
        Assert.Null(none.FirstValidationDate);
        Assert.Throws<ArgumentNullException>(() => RlPortfolioTransferAdmission.FirstValidationDate(null!, Array.Empty<DateTime>()));
    }

    // ----- fine-tune (the training run that starts from a stored base model) -----

    private RlPortfolioTrainingRequest FineTune(RlPanelSnapshot target, Guid baseRunId, RlPortfolioFreeze freeze = RlPortfolioFreeze.EncoderFrozen)
        => new(target.Directory, Costs(), _settings, SmokeOnly: true, Mode: RlPanelEvaluationMode.ShortHistory, Transfer: new RlPortfolioTransferInit(baseRunId, freeze));

    [Fact]
    public void AFreezeIsNamedByTheContractConstantsAndAnUnknownFreezeHasNoName()
    {
        Assert.Equal(RlPortfolioTransferContract.EncoderFrozen, RlPortfolioFreezes.Name(RlPortfolioFreeze.EncoderFrozen));
        Assert.Equal(RlPortfolioTransferContract.NoFreeze, RlPortfolioFreezes.Name(RlPortfolioFreeze.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioFreezes.Name((RlPortfolioFreeze)99));
        Assert.Null(new RlPortfolioTrainingRequest("panel", Costs(), _settings).Transfer);
        Assert.Null(new RlPortfolioRunRequest(ImmutableArray<string>.Empty, First, First.AddDays(1), false, RlPanelEvaluationMode.Standard, Costs(), false).Transfer);
    }

    [Fact]
    public async Task AFineTuneWhoseBaseReachesTheTargetValidationIsRejectedBeforeAnyRunFolderOrWorkerExists()
    {
        WriteWaves(2_400, Three);
        var baseId = await PublishBase(await Panel(Three, 1_445));                      // one day too long for a 2,300-bar target
        var target = await Panel(Three, 2_300);
        int runs = Store.List().Length;
        var panels = PanelFolders();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new RlPortfolioTrainingRunner(dataRoot: DataRoot).RunAsync(FineTune(target, baseId)));
        Assert.Equal($"{RlPortfolioTransferContract.ErrorPrefix} the base data reaches {First.AddDays(1_445):yyyy-MM-dd}, the target validation starts {First.AddDays(1_444):yyyy-MM-dd}.", error.Message);
        Assert.Equal(runs, Store.List().Length);                                          // no folder, not even an Incomplete one
        Assert.Equal(panels, PanelFolders());                                             // the runner never deletes a panel it was handed
        Assert.True(Directory.Exists(target.Directory));
    }

    [Fact]
    public async Task AFineTuneNeedsAVerifiedBaseRunAndAKnownFreezeAndTheServicePassesTheRequestOn()
    {
        WriteWaves(2_400, Three);
        var target = await Panel(Three, 2_300);
        int runs = Store.List().Length;
        var runner = new RlPortfolioTrainingRunner(dataRoot: DataRoot);
        var unknown = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(FineTune(target, Guid.NewGuid())));
        Assert.Equal($"{RlPortfolioTransferContract.ErrorPrefix} the base run is not a verified run.", unknown.Message);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(FineTune(target, Guid.NewGuid(), (RlPortfolioFreeze)99)));

        // The service builds the training request from its run request: the transfer reaches the runner (and its admission) unchanged.
        var request = Request(Three, 2_300) with { Transfer = new RlPortfolioTransferInit(Guid.NewGuid(), RlPortfolioFreeze.None) };
        var panels = PanelFolders();                                                      // the target above
        var viaService = await Assert.ThrowsAsync<InvalidDataException>(() => new RlPortfolioService(_settings, dataRoot: DataRoot).RunAsync(request, null, CancellationToken.None));
        Assert.Equal(unknown.Message, viaService.Message);
        Assert.True(RlPortfolioTransferRejection.Is(unknown) && RlPortfolioTransferRejection.Is(viaService));
        Assert.Equal(runs, Store.List().Length);
        Assert.Equal(panels, PanelFolders());                                             // the service removed the panel it captured for the rejected attempt, not the target of the caller
        Assert.True(Directory.Exists(target.Directory));
    }

    /// <summary>Compares two native weight files through the managed interpreter (bit-for-bit per tensor): encoder, head and key order.</summary>
    private static async Task<(bool KeysEqual, bool EncoderEqual, bool HeadEqual)> CompareNativeAsync(string baseNative, string tunedNative)
    {
        var (exitCode, text, errors) = await RlPortfolioPythonScript.RunAsync("rl_portfolio_state_compare.py", "--base", baseNative, "--tuned", tunedNative);
        Assert.True(exitCode == 0, "The compare script failed: " + errors);
        using var json = JsonDocument.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(line => line.StartsWith('{')));
        return (json.RootElement.GetProperty("keys_equal").GetBoolean(), json.RootElement.GetProperty("encoder_equal").GetBoolean(), json.RootElement.GetProperty("head_equal").GetBoolean());
    }

    /// <summary>
    /// Opt-in: a smoke-sized scratch run is the base; the fine-tune on a longer panel whose validation starts where the base data ends copies the
    /// base weights and scaler, trains the heads (and the encoder when not frozen) and publishes a lineage that validates.
    /// </summary>
    [RlPortfolioTrainingSmokeTests.SmokeFact]
    public async Task AFineTuneStartsFromTheBaseWeightsKeepsItsScalerAndPublishesItsLineage()
    {
        WriteWaves(2_400, Three);
        var runner = new RlPortfolioTrainingRunner(dataRoot: DataRoot);
        var pre = await runner.RunAsync(new RlPortfolioTrainingRequest((await Panel(Three, 1_444)).Directory, Costs(), _settings, SmokeOnly: true,
            Mode: RlPanelEvaluationMode.ShortHistory));
        var target = await Panel(Three, 2_300);
        var admitted = RlPortfolioTransferAdmission.Admit(Store, pre.RunId, FirstValidation(target));
        string baseNative = Path.Combine(pre.Directory, RlFiles.Native);

        foreach (var (freeze, encoderEqual) in new[] { (RlPortfolioFreeze.EncoderFrozen, true), (RlPortfolioFreeze.None, false) })
        {
            var tuned = await runner.RunAsync(FineTune(target, pre.RunId, freeze));
            var entry = Store.List().Single(e => e.RunId == tuned.RunId);
            Assert.Equal(RlPortfolioArtifactStatus.Verified, entry.Status);
            foreach (string name in new[] { RlPortfolioTransferContract.InitWeightsFile, RlPortfolioTransferContract.LineageFile, RlFiles.Native, RlFiles.Model,
                RlFiles.Model + RlFiles.ScalerSuffix, RlPortfolioEvaluationContract.ReportFile })
                Assert.Contains(name, entry.Manifest!.Files.Keys);
            Assert.DoesNotContain(RlPortfolioPartitionEvidence.FileName, entry.Manifest!.Files.Keys);

            // The base weights are copied byte for byte and the observation scale is the base's, not a refit on the new panel.
            Assert.Equal(ContentHash.OfFile(baseNative), ContentHash.OfFile(Path.Combine(tuned.Directory, RlPortfolioTransferContract.InitWeightsFile)));
            Assert.Equal(File.ReadAllBytes(pre.ModelPath + RlFiles.ScalerSuffix), File.ReadAllBytes(tuned.ModelPath + RlFiles.ScalerSuffix));
            using (var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(tuned.Directory, RlFiles.Job))))
            {
                var block = job.RootElement.GetProperty("transfer");
                Assert.Equal(RlPortfolioFreezes.Name(freeze), block.GetProperty("freeze").GetString());
                Assert.Equal(admitted.NativeSha256, block.GetProperty("init_weights_sha256").GetString());
            }

            var lineage = RlPortfolioTransferEvidence.Read(Path.Combine(tuned.Directory, RlPortfolioTransferContract.LineageFile));
            RlPortfolioTransferEvidence.Validate(lineage, admitted, FirstValidation(target));
            Assert.Equal((RlPortfolioTransferContract.FineTuneKind, RlPortfolioFreezes.Name(freeze), pre.RunId), (lineage.Kind, lineage.Freeze, lineage.BaseRunId));
            RlPortfolioEvaluationReports.Validate(RlPortfolioEvaluationReports.Read(tuned.ReportPath), target.Manifest, RlPanelEvaluationMode.ShortHistory,
                ContentHash.OfFile(tuned.ModelPath), Costs(), _settings.MaxWeightPercent);
            using (var model = new RlPortfolioActorModel(tuned.ModelPath))
            {
                Assert.Equal(Three.Length, model.MaxSymbols);
                model.ValidateParity(tuned.ParityPath);
            }

            // Frozen: every encoder tensor of the new weights equals the base's; the heads always moved away from the base.
            var (keysEqual, encoderSame, headSame) = await CompareNativeAsync(baseNative, Path.Combine(tuned.Directory, RlFiles.Native));
            Assert.True(keysEqual);
            Assert.Equal(encoderEqual, encoderSame);
            Assert.False(headSame);
        }
    }

    /// <summary>Opt-in: weights the worker cannot load fail the run inside the worker; the run folder stays without a manifest, with the failure text beside it.</summary>
    [RlPortfolioTrainingSmokeTests.SmokeFact]
    public async Task AFineTuneWhoseBaseWeightsCannotBeLoadedFailsInsideTheWorkerAndLeavesAnIncompleteRun()
    {
        WriteWaves(2_400, Three);
        var baseId = await PublishBase(await Panel(Three, 1_444));                      // a real store entry whose native weights are not a torch file
        var target = await Panel(Three, 2_300);
        await Assert.ThrowsAnyAsync<Exception>(() => new RlPortfolioTrainingRunner(dataRoot: DataRoot).RunAsync(FineTune(target, baseId)));
        var failed = Assert.Single(Store.List(), e => e.RunId != baseId);
        Assert.Equal(RlPortfolioArtifactStatus.Incomplete, failed.Status);
        Assert.Null(failed.Manifest);
        Assert.True(File.Exists(Path.Combine(failed.Directory, "failure.json")));
        Assert.True(File.Exists(Path.Combine(failed.Directory, RlPortfolioTransferContract.InitWeightsFile)));
        Assert.False(File.Exists(Path.Combine(failed.Directory, RlPortfolioTransferContract.LineageFile)));
        Assert.Empty(RlPortfolioBaseRuns.List(Store).Where(r => r.RunId == failed.RunId));
    }

    // ----- the scaler of a fine-tune comes from the copied sidecar (W1) -----

    /// <summary>A run folder that holds only the sidecar copied from the base (the file the run publishes), beside a base model written for <paramref name="panel"/>.</summary>
    private (string RunModel, string BaseModel, string BaseSha256) CopiedSidecar(RlPanelSnapshot panel)
    {
        string baseModel = WriteModel(panel, Path.Combine(_root, "base_" + Guid.NewGuid().ToString("N")));
        string runModel = Path.Combine(_root, "run_" + Guid.NewGuid().ToString("N"), RlFiles.Model);
        Directory.CreateDirectory(Path.GetDirectoryName(runModel)!);
        File.Copy(baseModel + RlFiles.ScalerSuffix, runModel + RlFiles.ScalerSuffix);
        return (runModel, baseModel, ContentHash.OfFile(baseModel));
    }

    [Fact]
    public async Task AFineTuneReadsItsScalerFromTheCopiedSidecarNotFromTheBaseFolder()
    {
        WriteWaves(1_400, Three);
        var (runModel, baseModel, baseSha) = CopiedSidecar(await Panel(Three, 1_400));
        var copied = RlPortfolioTrainingRunner.LoadCopiedScaler(runModel, baseModel, baseSha);
        Assert.Equal(0.05d, copied.Statistics[0].Std);

        // The base sidecar is replaced by another valid scaler after the copy: the statistics still are those of the copy.
        RlPortfolioScaler.Save(baseModel, RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(1d, 0.5d, -2d, 2d), 5).ToArray()));
        using (var changedBase = new RlPortfolioActorModel(baseModel)) Assert.Equal(0.5d, changedBase.Scaler.Statistics[0].Std);
        var again = RlPortfolioTrainingRunner.LoadCopiedScaler(runModel, baseModel, baseSha);
        Assert.Equal(copied.Statistics, again.Statistics);
    }

    [Fact]
    public async Task ABaseModelThatChangedAfterTheAdmissionIsRefusedBeforeItIsOpened()
    {
        WriteWaves(1_400, Three);
        var (runModel, baseModel, baseSha) = CopiedSidecar(await Panel(Three, 1_400));
        _ = RlPortfolioTrainingRunner.LoadCopiedScaler(runModel, baseModel, baseSha);        // unchanged: accepted
        using (var append = new FileStream(baseModel, FileMode.Append)) append.WriteByte(0);
        var error = Assert.Throws<InvalidDataException>(() => RlPortfolioTrainingRunner.LoadCopiedScaler(runModel, baseModel, baseSha));
        Assert.Equal($"{RlPortfolioTransferContract.ErrorPrefix} {RlFiles.Model} of the base run changed after the admission.", error.Message);
    }

    // ----- removal of the panel a rejected attempt captured (W3) -----

    [Fact]
    public void OnlyAnAdmissionOrSlotRejectionCarriesTheMarkNotAPlainOrALineageError()
    {
        Assert.False(RlPortfolioTransferRejection.Is(new InvalidDataException("plain")));
        Assert.False(RlPortfolioTransferRejection.Is(new IOException("not a rejection")));
        var marked = RlPortfolioTransferRejection.Create("marked");
        Assert.True(RlPortfolioTransferRejection.Is(marked));
        Assert.Equal(typeof(InvalidDataException), marked.GetType());                       // the type and the message stay those of a plain rejection
        Assert.Equal("marked", marked.Message);
        var unknown = Assert.Throws<InvalidDataException>(() => RlPortfolioTransferAdmission.Admit(Store, Guid.NewGuid(), First));
        Assert.True(RlPortfolioTransferRejection.Is(unknown));
    }

    [Fact]
    public void OnlyAGuidNamedPlainChildOfThePanelsRootIsRemovedAndItsContentGoesWithIt()
    {
        var store = Store;
        string own = store.PanelDirectory(Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(own, "sub"));
        File.WriteAllText(Path.Combine(own, RlPortfolioContract.PanelManifest), "{}");
        string neighbour = store.PanelDirectory(Guid.NewGuid());
        Directory.CreateDirectory(neighbour);
        Assert.True(store.TryRemoveCapturedPanel(own));
        Assert.False(Directory.Exists(own));
        Assert.True(Directory.Exists(neighbour));
        Assert.False(store.TryRemoveCapturedPanel(own));                                   // already gone
    }

    [Fact]
    public void APathOutsideThePanelsRootOrWithoutAGuidNameOrBelowAPanelIsNeverRemoved()
    {
        var store = Store;
        Directory.CreateDirectory(store.PanelsRoot);
        string named = Path.Combine(store.PanelsRoot, "not-a-guid");
        string run = Path.Combine(store.Root, Guid.NewGuid().ToString("N"));                 // a GUID name, but a run folder beside the panels root
        string panel = store.PanelDirectory(Guid.NewGuid());
        string nested = Path.Combine(panel, Guid.NewGuid().ToString("N"));
        string elsewhere = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        foreach (string folder in new[] { named, run, nested, elsewhere }) Directory.CreateDirectory(folder);
        foreach (string path in new[] { named, run, nested, elsewhere, store.PanelsRoot, store.Root, Path.Combine(store.PanelsRoot, ".."), "", "  ", null! })
            Assert.False(store.TryRemoveCapturedPanel(path), path);
        foreach (string folder in new[] { named, run, nested, elsewhere, store.PanelsRoot }) Assert.True(Directory.Exists(folder), folder);
        Assert.False(store.TryRemoveCapturedPanel(Path.Combine(store.PanelsRoot, Guid.NewGuid().ToString("N"))));        // absent folder
    }

    [Fact]
    public void ALinkedPanelFolderIsNeverFollowedOrRemoved()
    {
        var store = Store;
        string target = Path.Combine(_root, "link_target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Directory.CreateDirectory(store.PanelsRoot);
        string link = store.PanelDirectory(Guid.NewGuid());
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }       // this account may not create links: nothing to prove here
        Assert.False(store.TryRemoveCapturedPanel(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    /// <summary>
    /// Opt-in determinism check of the fine-tune on one machine: the same stored base, the same target panel, the same plan, seeds and freeze, run twice
    /// (once with the frozen encoder, once with nothing frozen). Every artifact the run produces is compared, and a difference is listed, never tolerated:
    /// it is a finding about this environment (the deterministic flags are necessary, not proven sufficient) and says nothing about other CPUs,
    /// operating systems or library versions. The ONNX file can be compared because both runs share one panel (its metadata carries the panel hashes).
    /// </summary>
    [RlPortfolioTrainingSmokeTests.SmokeFact]
    public async Task TwoFineTunesFromTheSameBaseOnTheSamePanelProduceTheSameModelAndEvidence()
    {
        WriteWaves(2_400, Three);
        var runner = new RlPortfolioTrainingRunner(dataRoot: DataRoot);
        var pre = await runner.RunAsync(new RlPortfolioTrainingRequest((await Panel(Three, 1_444)).Directory, Costs(), _settings, SmokeOnly: true,
            Mode: RlPanelEvaluationMode.ShortHistory));
        var target = await Panel(Three, 2_300);
        var differences = new List<string>();
        void Compare(string name, object? a, object? b) { if (!Equals(a, b)) differences.Add($"{name}: '{a}' != '{b}'"); }

        foreach (var freeze in new[] { RlPortfolioFreeze.EncoderFrozen, RlPortfolioFreeze.None })
        {
            var first = await runner.RunAsync(FineTune(target, pre.RunId, freeze));
            var second = await runner.RunAsync(FineTune(target, pre.RunId, freeze));
            Assert.NotEqual(first.Directory, second.Directory);
            string label = RlPortfolioFreezes.Name(freeze);
            Compare($"{label}: selected seed", first.SelectedSeed, second.SelectedSeed);
            Compare($"{label}: selected checkpoint", first.SelectedCheckpoint, second.SelectedCheckpoint);
            foreach (var (name, relative) in new[] { ("onnx model", RlFiles.Model), ("scaler sidecar", RlFiles.Model + RlFiles.ScalerSuffix), ("native state", RlFiles.Native),
                ("init weights", RlPortfolioTransferContract.InitWeightsFile), ("parity file", RlFiles.Parity), ("transfer.json", RlPortfolioTransferContract.LineageFile) })
                Compare($"{label}: {name} sha256", ContentHash.OfFile(Path.Combine(first.Directory, relative)), ContentHash.OfFile(Path.Combine(second.Directory, relative)));

            // transfer.json holds no run id and no time of its own run (its run id is the base's), so the documents are compared as a whole.
            Compare($"{label}: lineage document",
                RlPortfolioTransferEvidence.Read(Path.Combine(first.Directory, RlPortfolioTransferContract.LineageFile)),
                RlPortfolioTransferEvidence.Read(Path.Combine(second.Directory, RlPortfolioTransferContract.LineageFile)));

            // The selection evidence: every complete candidate in selection order (seed, checkpoint, validation mean), and the transitions per seed.
            RlPortfolioTrainingSummary Summary(RlPortfolioTrainingResult r)
                => JsonSerializer.Deserialize<RlPortfolioTrainingSummary>(File.ReadAllText(Path.Combine(r.Directory, RlPortfolioTrainingSummaries.FileName)), RlFiles.Json)!;
            var a = Summary(first); var b = Summary(second);
            Compare($"{label}: candidate count", a.Candidates.Length, b.Candidates.Length);
            for (int i = 0; i < Math.Min(a.Candidates.Length, b.Candidates.Length); i++)
                Compare($"{label}: candidate {i} (seed, checkpoint, mean)", (a.Candidates[i].Seed, a.Candidates[i].Checkpoint, a.Candidates[i].MeanReturn),
                    (b.Candidates[i].Seed, b.Candidates[i].Checkpoint, b.Candidates[i].MeanReturn));
            Compare($"{label}: training transitions per seed", string.Join(",", a.SeedProgress.Select(p => p.TrainingTransitions)), string.Join(",", b.SeedProgress.Select(p => p.TrainingTransitions)));

            Compare($"{label}: evaluation cell count", first.Evaluation.Cells.Length, second.Evaluation.Cells.Length);
            for (int i = 0; i < Math.Min(first.Evaluation.Cells.Length, second.Evaluation.Cells.Length); i++)
            {
                var x = first.Evaluation.Cells[i]; var y = second.Evaluation.Cells[i];
                Compare($"{label}: cell {x.Partition}/{x.Group} policy distribution", x.Policy, y.Policy);
            }
        }
        Assert.True(differences.Count == 0, "Two fine-tunes from the same base on the same panel differ in " + differences.Count + " item(s):" + Environment.NewLine + string.Join(Environment.NewLine, differences));
    }

    // ----- a run on a panel an earlier run stored (TL-E1) -----

    private static Guid PanelIdOf(string panelDirectory) => Guid.ParseExact(Path.GetFileName(panelDirectory), "N");

    [Fact]
    public void AStoredPanelIsLocatedByItsFolderNameAndAnythingElseIsRefused()
    {
        var store = Store;
        string own = store.PanelDirectory(Guid.NewGuid());
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, RlPortfolioContract.PanelManifest), "{}");
        Assert.Equal(own, store.ExistingPanelDirectory(PanelIdOf(own)));
        string withoutManifest = store.PanelDirectory(Guid.NewGuid());
        Directory.CreateDirectory(withoutManifest);
        foreach (var id in new[] { Guid.NewGuid(), PanelIdOf(withoutManifest) })
            Assert.StartsWith(RlPortfolioContract.UniverseErrorPrefix, Assert.Throws<InvalidDataException>(() => store.ExistingPanelDirectory(id)).Message);
        Assert.Throws<ArgumentException>(() => store.ExistingPanelDirectory(Guid.Empty));
        Assert.Null(new RlPortfolioRunRequest(ImmutableArray<string>.Empty, First, First.AddDays(1), false, RlPanelEvaluationMode.Standard, Costs(), false).PanelId);
    }

    [Fact]
    public async Task TwoEvaluationsOnOneStoredPanelShareTheHashesAndCompareWhileACapturedPanelDoesNot()
    {
        WriteWaves(2_400, Three);
        var baseId = await PublishBase(await Panel(Three, 1_444));
        var service = Service();
        var first = await service.EvaluateStoredModelAsync(baseId, Request(Three, 2_300), CancellationToken.None);          // captures its own panel
        int panels = PanelFolders().Length;
        // The stored panel decides: the symbols, dates and holdout of the request are ignored (here: no symbols at all).
        var again = Request(Array.Empty<string>(), 1) with { PanelId = PanelIdOf(first.PanelDirectory) };
        var second = await service.EvaluateStoredModelAsync(baseId, again, CancellationToken.None);
        Assert.Equal(panels, PanelFolders().Length);                                          // nothing was captured
        Assert.Equal(first.PanelDirectory, second.PanelDirectory);
        Assert.Equal((first.Evaluation.UniverseSha256, first.Evaluation.CalendarSha256), (second.Evaluation.UniverseSha256, second.Evaluation.CalendarSha256));
        var comparison = RlPortfolioTransferComparison.Compare(first.Evaluation, second.Evaluation);
        Assert.All(comparison.Cells.Where(c => c.Comparable), c => Assert.Equal((0m, 0), (c.PairedDelta!.Mean, c.Losses + c.Wins)));       // the same model on the same blocks: no difference

        // A second capture of the same symbols never shares the hashes, so the reports of two captures cannot be paired.
        var other = await service.EvaluateStoredModelAsync(baseId, Request(Three, 2_300), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(first.Evaluation, other.Evaluation));
    }

    [Fact]
    public async Task ARejectedRunOnAStoredPanelNeverRemovesThatPanel()
    {
        WriteWaves(2_400, Three);
        var baseId = await PublishBase(await Panel(Three, 1_445));                         // one day too long for a 2,300-bar target
        var stored = await Panel(Three, 2_300);
        var panels = PanelFolders();
        var request = Request(Three, 2_300) with { PanelId = PanelIdOf(stored.Directory) };
        var viaTransfer = await Assert.ThrowsAsync<InvalidDataException>(() => Service().EvaluateStoredModelAsync(baseId, request, CancellationToken.None));
        Assert.True(RlPortfolioTransferRejection.Is(viaTransfer));
        var viaRun = await Assert.ThrowsAsync<InvalidDataException>(() => new RlPortfolioService(_settings, dataRoot: DataRoot)
            .RunAsync(request with { Transfer = new RlPortfolioTransferInit(baseId, RlPortfolioFreeze.EncoderFrozen) }, null, CancellationToken.None));
        Assert.True(RlPortfolioTransferRejection.Is(viaRun));
        Assert.Equal(viaTransfer.Message, viaRun.Message);
        Assert.Equal(panels, PanelFolders());
        Assert.True(Directory.Exists(stored.Directory));
    }

    [Fact]
    public async Task AnUnknownStoredPanelIsRefusedBeforeAnythingIsCapturedOrStarted()
    {
        WriteWaves(2_400, Three);
        var baseId = await PublishBase(await Panel(Three, 1_444));
        var panels = PanelFolders();
        int runs = Store.List().Length;
        var request = Request(Three, 2_300) with { PanelId = Guid.NewGuid() };
        var viaTransfer = await Assert.ThrowsAsync<InvalidDataException>(() => Service().EvaluateStoredModelAsync(baseId, request, CancellationToken.None));
        var viaRun = await Assert.ThrowsAsync<InvalidDataException>(() => new RlPortfolioService(_settings, dataRoot: DataRoot).RunAsync(request, null, CancellationToken.None));
        Assert.All(new[] { viaTransfer, viaRun }, e => Assert.Contains("does not exist", e.Message));
        Assert.Equal(panels, PanelFolders());
        Assert.Equal(runs, Store.List().Length);
    }

    /// <summary>
    /// Opt-in: a from-scratch run and a fine-tune of the same base run on ONE stored panel share the universe and calendar hashes and the blocks, so their
    /// reports can be paired cell by cell; the validation cell is flagged as the one that chose the checkpoint. The result says nothing about whether transfer helps.
    /// </summary>
    [RlPortfolioTrainingSmokeTests.SmokeFact]
    public async Task AScratchRunAndAFineTuneOnOneStoredPanelCanBeComparedCellByCell()
    {
        WriteWaves(2_400, Three);
        var runner = new RlPortfolioTrainingRunner(dataRoot: DataRoot);
        var pre = await runner.RunAsync(new RlPortfolioTrainingRequest((await Panel(Three, 1_444)).Directory, Costs(), _settings, SmokeOnly: true,
            Mode: RlPanelEvaluationMode.ShortHistory));
        var service = new RlPortfolioService(_settings, dataRoot: DataRoot);
        var scratch = await service.RunAsync(Request(Three, 2_300) with { SmokeOnly = true }, null, CancellationToken.None);       // captures the panel
        var tuned = await service.RunAsync(Request(Three, 2_300) with { SmokeOnly = true, PanelId = PanelIdOf(scratch.PanelDirectory),
            Transfer = new RlPortfolioTransferInit(pre.RunId, RlPortfolioFreeze.EncoderFrozen) }, null, CancellationToken.None);
        Assert.Equal(scratch.PanelDirectory, tuned.PanelDirectory);
        var comparison = RlPortfolioTransferComparison.Compare(tuned.Evaluation, scratch.Evaluation);
        Assert.Equal(scratch.Evaluation.Cells.Length, comparison.Cells.Length);
        Assert.Contains(comparison.Cells, c => c.Comparable);
        Assert.All(comparison.Cells.Where(c => c.Comparable), c =>
        {
            Assert.Equal(c.Wins + c.Ties + c.Losses, c.BlockCount);
            Assert.Equal(c.Partition == RlPortfolioContract.ValidationPartition && c.Group == RlPanelSymbolGroup.Train, c.SelectionContaminated);
        });
    }

    // ----- what the dialog lists and compares (TL-E2) -----

    [Fact]
    public async Task TheStoredPanelsAreListedWithTheirFactsAndFoldersThatAreNotPanelsAreSkipped()
    {
        string[] four = { "WAVA", "WAVB", "WAVC", "WAVD" };
        WriteWaves(2_400, four);
        var plain = await Panel(four, 1_444);
        var held = await Panel(four, 2_300, holdout: true);
        Directory.CreateDirectory(Path.Combine(Store.PanelsRoot, "not-a-guid"));                                            // not a panel id
        Directory.CreateDirectory(Store.PanelDirectory(Guid.NewGuid()));                                                      // no manifest
        var service = Service();
        var panels = service.ListStoredPanels();
        Assert.Equal(2, panels.Length);
        var a = panels.Single(p => p.PanelId == PanelIdOf(plain.Directory));
        var b = panels.Single(p => p.PanelId == PanelIdOf(held.Directory));
        Assert.Equal((4, 0, First, First.AddDays(1_444)), (a.SymbolCount, a.HoldoutCount, a.PeriodStartInclusive, a.PeriodEndExclusive));
        Assert.Equal((4, held.Manifest.HoldoutSymbols.Length, First.AddDays(2_300)), (b.SymbolCount, b.HoldoutCount, b.PeriodEndExclusive));
        Assert.True(b.HoldoutCount > 0);
        Assert.Equal((held.Manifest.UniverseSha256, held.Manifest.CalendarSha256), (b.UniverseSha256, b.CalendarSha256));
        Assert.Empty(new RlPortfolioTransferService(_settings, dataRoot: Path.Combine(_root, "elsewhere")).ListStoredPanels());
        Assert.Throws<ArgumentNullException>(() => RlPortfolioStoredRuns.ListPanels(null!));
    }

    [Fact]
    public async Task TheFirstValidationDateOfAStoredPanelIsTheOneAFreshCaptureWouldHaveAndAnUnusablePanelIsRefusedWithItsReason()
    {
        WriteWaves(2_400, Three);
        var service = Service();
        var panel = await Panel(Three, 2_300);
        Assert.Equal(FirstValidation(panel), service.StoredPanelFirstValidationDate(PanelIdOf(panel.Directory), RlPanelEvaluationMode.ShortHistory, null));
        var blockCount = new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(1, 1, 1));
        var viaPlan = service.StoredPanelFirstValidationDate(PanelIdOf(panel.Directory), RlPanelEvaluationMode.ShortHistory, blockCount);
        Assert.Equal(FirstValidation(await Panel(Three, 2_300, blockCount)), viaPlan);                                         // the plan only recomputes the catalog
        Assert.NotEqual(FirstValidation(panel), viaPlan);
        Assert.Throws<InvalidDataException>(() => service.StoredPanelFirstValidationDate(Guid.NewGuid(), RlPanelEvaluationMode.ShortHistory, null));      // unknown panel
        Assert.Throws<InvalidDataException>(() => service.StoredPanelFirstValidationDate(PanelIdOf(panel.Directory), RlPanelEvaluationMode.Standard, null));   // too short for the standard rule
        Assert.Throws<ArgumentException>(() => service.StoredPanelFirstValidationDate(PanelIdOf(panel.Directory), RlPanelEvaluationMode.Standard, blockCount));  // a plan needs the short-history mode
    }

    [Fact]
    public async Task RunsWithAReportAreListedByKindAndTwoOfThemOnOnePanelAreComparedThroughTheService()
    {
        WriteWaves(2_400, Three);
        var baseId = await PublishBase(await Panel(Three, 1_444));
        var service = Service();
        var first = await service.EvaluateStoredModelAsync(baseId, Request(Three, 2_300), CancellationToken.None);
        var second = await service.EvaluateStoredModelAsync(baseId, Request(Three, 1) with { PanelId = PanelIdOf(first.PanelDirectory) }, CancellationToken.None);
        // A run without a lineage is a from-scratch run (here: the same report published without one); an unreadable lineage leaves a run out; a run without a report is not listed.
        string universe = first.Evaluation.UniverseSha256, calendar = first.Evaluation.CalendarSha256;
        async Task<Guid> PublishRun(string? lineage)
        {
            var id = Guid.NewGuid();
            string directory = Store.Begin(id);
            File.Copy(first.ReportPath, Path.Combine(directory, RlPortfolioEvaluationContract.ReportFile));
            var names = new List<string> { RlPortfolioEvaluationContract.ReportFile };
            if (lineage is not null) { File.WriteAllText(Path.Combine(directory, RlPortfolioTransferContract.LineageFile), lineage); names.Add(RlPortfolioTransferContract.LineageFile); }
            await Store.PublishAsync(id, universe, calendar, names);
            return id;
        }
        var scratch = await PublishRun(null);
        var unreadable = await PublishRun("{ not json");

        var runs = service.ListComparableRuns();
        Assert.DoesNotContain(runs, r => r.RunId == baseId || r.RunId == unreadable);                                          // no report / unreadable lineage
        Assert.Equal(RlPortfolioComparableRun.ScratchKind, runs.Single(r => r.RunId == scratch).Kind);
        Assert.All(new[] { first.RunId, second.RunId }, id => Assert.Equal(RlPortfolioTransferContract.ZeroShotKind, runs.Single(r => r.RunId == id).Kind));
        Assert.All(runs, r => Assert.Equal((universe, calendar), (r.UniverseSha256, r.CalendarSha256)));
        Assert.Equal(3, runs.Length);

        var result = service.CompareRuns(second.RunId, scratch);                                                                // the same blocks and model: no difference
        Assert.Equal(first.Evaluation.Cells.Length, result.Cells.Length);
        Assert.All(result.Cells.Where(c => c.Comparable), c => Assert.Equal(0m, c.PairedDelta!.Mean));
        Assert.Equal(RlPortfolioTransferContract.Statement, result.Statement);

        // Another capture of the same symbols cannot be paired; a run that is not verified, not there or without a report is refused by name.
        var other = await service.EvaluateStoredModelAsync(baseId, Request(Three, 2_300), CancellationToken.None);
        Assert.Contains("the universe hash", Assert.Throws<InvalidDataException>(() => service.CompareRuns(first.RunId, other.RunId)).Message);
        Assert.Contains("is not a verified run with an evaluation report", Assert.Throws<InvalidDataException>(() => service.CompareRuns(first.RunId, Guid.NewGuid())).Message);
        Assert.Contains("is not a verified run with an evaluation report", Assert.Throws<InvalidDataException>(() => service.CompareRuns(baseId, first.RunId)).Message);
        Assert.Throws<ArgumentNullException>(() => RlPortfolioStoredRuns.Compare(null!, first.RunId, second.RunId));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioStoredRuns.ListComparableRuns(null!));
    }

    [Fact]
    public void AServiceThatPredatesTheStoredPanelAndComparisonMembersStillCompilesAndOffersNone()
    {
        IRlPortfolioTransferService older = new OlderTransferService();
        Assert.Empty(older.ListStoredPanels());
        Assert.Empty(older.ListComparableRuns());
        Assert.Throws<NotSupportedException>(() => older.StoredPanelFirstValidationDate(Guid.NewGuid(), RlPanelEvaluationMode.ShortHistory, null));
        Assert.Throws<NotSupportedException>(() => older.CompareRuns(Guid.NewGuid(), Guid.NewGuid()));
    }

    // ----- the symbol-overlap report (W7): a number from two stored manifests, never a rejection -----

    /// <summary>A service written before <c>SymbolOverlap</c> existed: it implements only the original members.</summary>
    private sealed class OlderTransferService : IRlPortfolioTransferService
    {
        public ImmutableArray<RlPortfolioBaseRunInfo> ListBaseRuns() => ImmutableArray<RlPortfolioBaseRunInfo>.Empty;
        public RlPortfolioBaseRun Admit(Guid baseRunId, DateOnly targetFirstValidationDate) => throw new NotSupportedException();
        public Task<RlPortfolioZeroShotResult> EvaluateStoredModelAsync(Guid baseRunId, RlPortfolioRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void AServiceThatPredatesTheOverlapReportStillCompilesAndReportsNone()
        => Assert.Null(((IRlPortfolioTransferService)new OlderTransferService()).SymbolOverlap(Guid.NewGuid(), "any"));

    [Fact]
    public async Task ThePanelManifestOfARunIsFoundOnlyWhenItIsUnambiguous()
    {
        WriteWaves(2_400, Three);
        var panel = await Panel(Three, 1_444);
        var found = RlPortfolioBaseRuns.PanelManifest(Store, panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256);
        Assert.NotNull(found);
        Assert.Equal(Three, found!.Symbols.Select(e => e.Symbol).ToArray());
        Assert.Equal((panel.Manifest.PeriodStartInclusive, panel.Manifest.PeriodEndExclusive), (found.PeriodStartInclusive, found.PeriodEndExclusive));
        Assert.Null(RlPortfolioBaseRuns.PanelManifest(Store, new string('0', 64), panel.Manifest.CalendarSha256));          // unknown hashes
        ForgePanelWithAnotherPeriod(panel, 56);                                                                               // same hashes, another period end
        Assert.Null(RlPortfolioBaseRuns.PanelManifest(Store, panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioBaseRuns.PanelManifest(null!, "a", "b"));
    }

    [Fact]
    public async Task TheServiceReportsTheHeldOutSymbolsTheBaseMayHaveSeenAndNothingWhenAPanelIsMissing()
    {
        string[] four = { "WAVA", "WAVB", "WAVC", "WAVD" };               // a holdout needs at least four symbols
        WriteWaves(2_400, four);
        var basePanel = await Panel(four, 1_444);                                        // no holdout: every symbol was in the base training group
        var baseId = await PublishBase(basePanel);
        var target = await Panel(four, 2_300, holdout: true);
        Assert.NotEmpty(target.Manifest.HoldoutSymbols);                                  // the report below is meaningful only with held-out symbols
        var service = Service();
        var overlap = service.SymbolOverlap(baseId, target.Directory);
        Assert.NotNull(overlap);
        Assert.Equal(four, overlap!.BaseTrainingGroupSymbols.ToArray());
        Assert.Equal(target.Manifest.HoldoutSymbols.Order(StringComparer.Ordinal), overlap.TargetHeldOutSymbols.ToArray());
        Assert.Equal(overlap.TargetHeldOutSymbols.ToArray(), overlap.HeldOutSeenByBase.ToArray());                         // all of them were in the base
        Assert.Equal(four, overlap.TargetSymbolsSeenByBase.ToArray());

        // A target without a holdout reports "0 of 0".
        var plain = service.SymbolOverlap(baseId, (await Panel(four, 2_300)).Directory)!;
        Assert.Empty(plain.TargetHeldOutSymbols); Assert.Empty(plain.HeldOutSeenByBase);

        // Nothing is reported (and nothing thrown) when the base run, its panel or the target panel is not available.
        Assert.Null(service.SymbolOverlap(Guid.NewGuid(), target.Directory));
        Assert.Null(service.SymbolOverlap(baseId, Path.Combine(_root, "no_such_panel")));
        Directory.Delete(basePanel.Directory, recursive: true);
        Assert.Null(service.SymbolOverlap(baseId, target.Directory));
        Assert.Throws<ArgumentException>(() => service.SymbolOverlap(baseId, " "));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => RlPortfolioBaseRuns.List(null!));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioTransferAdmission.Admit(null!, Guid.NewGuid(), First));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioTransferAdmission.FirstValidationDate(null!));
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioTransferService(null!));
    }
}
