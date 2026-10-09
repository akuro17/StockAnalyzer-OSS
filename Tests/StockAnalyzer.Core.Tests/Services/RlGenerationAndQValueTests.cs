using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Numerical ONNX/source/evaluation fixtures test integrity and lifecycle only; they are never trained-market qualification evidence.</summary>
[Collection("ChildProcess")]
public sealed partial class RlGenerationAndQValueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_generation_contract_" + Guid.NewGuid().ToString("N"));
    private const string Symbol = "NUMERIC_TEST";
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime Start = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TrainingResourceLimits Limits = new(TrainingResourceLimits.CurrentContractVersion,
        TrainingResourceOverrides.MaximumChannels, TrainingResourceOverrides.MaximumTensorSizeMiB,
        TrainingResourceOverrides.MaximumSamples, TrainingResourceOverrides.MaximumEvaluationFolds,
        TrainingResourceOverrides.MaximumFeatureLag, TrainingResourceOverrides.MaximumEnsembleMembers,
        TrainingResourceOverrides.MaximumRunDurationMinutes * 60);
    private string RegistryRoot => Path.Combine(_root, "registry");

    private (string Directory, RlSourceSnapshot Source, FixedScaler Scaler) Candidate(decimal score = -0.01m, bool tieFixture = false,
        bool shortHistory = false, int barCount = 5_200)
    {
        string directory = Path.Combine(_root, "candidate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Complete bounded numerical rows establish contract support; no fixture claims vendor acquisition or profitable training.
        var bars = Enumerable.Range(0, barCount).Select(i => new CandleData(Start.AddDays(i), 100m, 110m, 90m, 100m, 1_000)).ToImmutableArray();
        Write(Path.Combine(directory, RlFiles.Bars), bars);
        Write(Path.Combine(directory, RlFiles.Actions), Array.Empty<RlCorporateAction>());
        CreateParquet(Path.Combine(directory, RlFiles.RawParquet),
            $"SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({bars.Length})");
        CreateParquet(Path.Combine(directory, RlFiles.MetadataParquet),
            "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        var stored = RlLocalParquetReader.Read(Path.Combine(directory, RlFiles.RawParquet), new(2000,1,1), new(2026,1,1), Limits, CancellationToken.None);
        var parameters = RlSourceContract.Acquisition.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
        var manifest = new RlSourceManifest(RlSourceContract.SchemaVersion, RlFiles.LocalProvider, RlFiles.LocalProviderVersion, Symbol, "USD", "UTC",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero),
            ContentHash.OfFile(Path.Combine(directory, RlFiles.Bars)), ContentHash.OfFile(Path.Combine(directory, RlFiles.Actions)),
            bars.Length, RlSourceContract.Adjustment, RlSourceContract.LocalProvenance, RlSourceContract.Unverified, stored.StorageDtype,
            RlSourceContract.FinalityPolicy, new DateOnly(2026, 1, 1), new DateOnly(2000, 1, 1), new DateOnly(2026, 1, 1), parameters)
            { InstrumentType = "EQUITY", MappedSymbol = Symbol, SourcePathIdentity = "Daily/" + Symbol + ".parquet",
                SourceFileSha256 = ContentHash.OfFile(Path.Combine(directory, RlFiles.RawParquet)), CurrentHistoryRevision = false,
                MetadataPathIdentity = "Metadata/" + Symbol + ".meta.parquet", MetadataFileSha256 = ContentHash.OfFile(Path.Combine(directory, RlFiles.MetadataParquet)),
                SourceRows = stored.SourceRows, EvidencePolicy = RlSourceContract.EvidencePolicy,
                OriginalAcquisitionStatus = RlSourceContract.Unverified, CorporateActionCoverageStatus = RlSourceContract.Unverified,
                DateLabelSemantics = RlSourceContract.DateLabelSemantics,
                FirstDate = DateOnly.FromDateTime(bars[0].Timestamp), LastDate = DateOnly.FromDateTime(bars[^1].Timestamp),
                ActionsPeriodStart = null, ActionsPeriodEnd = null };
        Write(Path.Combine(directory, RlFiles.SourceManifest), manifest);
        // The saved job selects the evaluation specification; the complete job document replaces this stub below.
        if (shortHistory) Write(Path.Combine(directory, RlFiles.Job), new Dictionary<string, object> { [RlShortHistoryEvaluationContract.MetadataKey] = RlShortHistoryEvaluationContract.SpecificationId });
        var source = RlSourceSnapshot.Load(directory, Limits);
        Write(Path.Combine(directory, RlFiles.Catalog), source.Catalog);
        var actions = source.Catalog.Train.Concat(source.Catalog.Validation).Concat(source.Catalog.Oos)
            .ToDictionary(block => block.Id, _ => new int[RlContract.EpisodeHorizon]);
        Write(Path.Combine(directory, RlFiles.BaselineActions), actions);
        var metadata = RlObservationBuilder.CreateMetadata(Symbol, source.Revision, Revision);
        if (shortHistory) metadata[RlShortHistoryEvaluationContract.MetadataKey] = RlShortHistoryEvaluationContract.SpecificationId;
        byte[] model =File.ReadAllBytes(Path.Combine("Assets", "rl_numeric_q_fixture.onnx"));
        if (tieFixture)
        {
            byte[] bias = new byte[3 * sizeof(float)];
            Buffer.BlockCopy(new[] { 0f, 1f, -1f }, 0, bias, 0, bias.Length);
            int biasIndex = model.AsSpan().IndexOf(bias);
            Assert.True(biasIndex >= 0);
            Buffer.BlockCopy(new[] { 1f, 1f, -1f }, 0, model, biasIndex, bias.Length);
        }
        using (var output = File.Create(Path.Combine(directory, RlFiles.Model)))
        {
            output.Write(model);
            foreach (var pair in metadata)
            {
                using var entry = new MemoryStream();
                WriteProtoString(entry, 1, pair.Key); WriteProtoString(entry, 2, pair.Value);
                WriteVarint(output, (14 << 3) | 2); WriteVarint(output, checked((int)entry.Length)); entry.Position = 0; entry.CopyTo(output);
            }
        }
        var scaler = new FixedScaler(FixedScaler.CurrentSchemaVersion, RlFiles.FeatureMode, RlContract.WindowLength,
            Array.Empty<int>(), null, FixedScaler.ChannelNames(metadata, Array.Empty<int>()),
            Enumerable.Repeat(new FixedScalerStatistic(100d, 1d, 0d, 1_000d), RlContract.MarketChannelCount).ToArray(),
            FixedScaler.ComputeContractHash(metadata));
        Write(Path.Combine(directory, RlFiles.Model + RlFiles.ScalerSuffix), scaler);
        File.WriteAllText(Path.Combine(directory, RlFiles.Native), "numerical_fixture_native_placeholder_not_training_evidence");
        Write(Path.Combine(directory, RlFiles.Dependencies), new { numeric_fixture = true,
            packages = new Dictionary<string, string> { ["stable-baselines3"] = RlContract.StableBaselinesVersion, ["gymnasium"] = RlContract.GymnasiumVersion },
            torch_device = "cpu", deterministic_algorithms = true, source_script_sha256 = Revision });
        var costs = RlContract.CreateConfiguration(new BacktestConfiguration
        { InitialCapital = 1_000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m });
        var jobDocument = new Dictionary<string, object> { ["source_directory"] = directory, ["costs"] = costs, ["defaults"] = RlContract.ToWireDefaults(),
            ["resource_limits"] = Limits, ["smoke_only"] = false };
        if (shortHistory)
        {
            jobDocument[RlShortHistoryEvaluationContract.MetadataKey] = RlShortHistoryEvaluationContract.SpecificationId;
            jobDocument[RlShortHistoryEvaluationContract.ContractKey] = new { specification_id = RlShortHistoryEvaluationContract.SpecificationId,
                schema_version = RlShortHistoryEvaluationContract.SchemaVersion, contract_hash = RlShortHistoryEvaluationContract.ContractHash };
            jobDocument["evaluation_resource_estimate"] = RlConstraintEvaluation.ToWire(RlShortHistoryEvaluation.Estimate(source, false));
        }
        Write(Path.Combine(directory, RlFiles.Job), jobDocument);
        using var session = new BacktestEngine(new IndicatorFactory()).CreateRlSession(source.CreateInput(source.Catalog.Validation[0]), costs);
        float[] observation = RlObservationBuilder.Build(session.Reset(), scaler);
        Write(Path.Combine(directory, RlFiles.Parity), new { observations = new[] { observation }, q_values = new[] { tieFixture ? new[] { 1f, 1f, -1f } : new[] { 0f, 1f, -1f } } });
        Dictionary<string, object> Episode(RlEpisodeBlock block) => new()
        {
            ["block_id"] = block.Id, ["partition"] = block.Partition, ["terminal_return"] = Text(score),
            ["maximum_drawdown"] = "0.01", ["completed_decisions"] = RlContract.EpisodeHorizon,
            ["terminal_reason"] = nameof(RlTerminalReason.Horizon), ["residual_quantity"] = "0", ["realized_pnl"] = "-10",
            ["unrealized_pnl"] = "0", ["costs"] = "0",
        };
        object[] Episodes(IEnumerable<RlEpisodeBlock> blocks) => blocks.Select(b => (object)Episode(b)).ToArray();
        Dictionary<string, object> Baselines(ImmutableArray<RlEpisodeBlock> blocks)
        {
            var report = new Dictionary<string, object>();
            foreach (string policy in new[] { RlContract.SmaBaselineId, RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId })
                report[policy] = new { episodes = Episodes(blocks), mean_return = Text(score), mean_maximum_drawdown = "0.01", worst_maximum_drawdown = "0.01" };
            report[RlContract.RandomBaselineId] = new
            {
                mean_return = Text(score), min_seed_return = Text(score), max_seed_return = Text(score),
                seeds = RlContract.CandidateSeeds.Select(seed => new { seed = seed + RlContract.RandomBaselineSeedOffset, episodes = Episodes(blocks), mean_return = Text(score) }).ToArray(),
            };
            return report;
        }
        var report = new Dictionary<string, object>
        {
            ["schema_version"] = RlContract.SchemaVersion, ["smoke_only"] = false, ["contract_hash"] = RlContract.ContractHash,
            ["source_revision"] = source.Revision, ["evaluation_revision"] = Revision, ["symbol"] = Symbol,
            ["model_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlFiles.Model)), ["parity_verified"] = true,
            ["validation_mean_return"] = Text(score), ["oos_mean_return"] = Text(score),
            ["validation_episodes"] = Episodes(source.Catalog.Validation), ["oos_episodes"] = Episodes(source.Catalog.Oos),
            ["candidate_seed"] = RlContract.CandidateSeeds[0], ["checkpoint_step"] = RlContract.ValidationCheckpointInterval,
            ["candidate_evaluations"] = RlContract.CandidateSeeds.SelectMany(seed => new[] { RlContract.ValidationCheckpointInterval, RlContract.TrainingTransitionsPerSeed }
                .Select(checkpoint => new { seed, checkpoint_step = checkpoint, summary = new { episodes = Episodes(source.Catalog.Validation), mean_return = Text(score) } })).ToArray(),
            ["configuration"] = costs, ["resources"] = new { processed_transitions = RlContract.TrainingTransitionsPerSeed * RlContract.CandidateSeeds.Length, episode_starts = 300 },
            ["baselines"] = new { validation = Baselines(source.Catalog.Validation), oos = Baselines(source.Catalog.Oos) },
        };
        if (shortHistory)
        {
            RlEpisodeEvaluation[] Rows(IEnumerable<RlEpisodeBlock> blocks) => blocks.Select(b => new RlEpisodeEvaluation(b.Id, b.Partition, Text(score), "0.01",
                RlContract.EpisodeHorizon, nameof(RlTerminalReason.Horizon), "0", "-10", "0", "0", 0, 0)).ToArray();
            report[RlShortHistoryEvaluationContract.MetadataKey] = RlShortHistoryEvaluationContract.SpecificationId;
            report[RlShortHistoryEvaluationContract.ReportKey] = RlConstraintEvaluation.ToWire(
                RlShortHistoryEvaluation.Create(source, Rows(source.Catalog.Validation), Rows(source.Catalog.Oos), false));
        }
        Write(Path.Combine(directory, RlFiles.Model + RlFiles.EvaluationSuffix), report);
        return (directory, source, scaler);
    }

    [Fact]
    public async Task RegisterAndManualActivate_KeepSupervisedPointerSeparateAndAllowNegativeScores()
    {
        var candidate = Candidate();
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        Assert.Null(registry.ActiveId);
        Assert.Null(registry.RlActiveId);
        Assert.Empty(registry.List());
        Assert.Equal(-0.01m, generation.ValidationReturn);
        Assert.Single(registry.ListRl());
        Assert.Null(registry.AcquireRlActive());
        Assert.True(await registry.ActivateRlAsync(generation.ModelId));
        Assert.False(await registry.ActivateRlAsync(generation.ModelId));
        using var lease = registry.AcquireRlActive();
        Assert.Equal(generation.ModelId, lease!.ModelId);
        generation.Files.Clear();
        Assert.NotEmpty(registry.GetRlGeneration(generation.ModelId)!.Files);
        Assert.Null(registry.ActiveId);
        using var restarted = new ModelGenerationRegistry(RegistryRoot);
        Assert.Equal(generation.ModelId, restarted.RlActiveId);
    }

    [Fact]
    public async Task MissingSmokeBadMeanAndParityCandidates_AreUnavailableWithoutPublication()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var missing = Candidate(); File.Delete(Path.Combine(missing.Directory, RlFiles.Native));
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.RegisterRlAsync(missing.Directory));
        var smoke = Candidate(); Change(smoke.Directory, RlFiles.Model + RlFiles.EvaluationSuffix, root => root["smoke_only"] = true);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(smoke.Directory));
        var badMean = Candidate(); Change(badMean.Directory, RlFiles.Model + RlFiles.EvaluationSuffix, root => root["validation_mean_return"] = "99");
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(badMean.Directory));
        var parity = Candidate(); Change(parity.Directory, RlFiles.Parity, root => root["q_values"]![0]![1] = 2f);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(parity.Directory));
        Assert.Empty(registry.ListRl());
        Assert.Null(registry.RlActiveId);
    }

    [Fact]
    public async Task ExactActionParity_RejectsTolerancePassingNearTieFlip()
    {
        var candidate = Candidate(tieFixture: true);
        Change(candidate.Directory, RlFiles.Parity, root => root["q_values"]![0] = JsonSerializer.SerializeToNode(new[] { 1f, MathF.BitIncrement(1f), -1f }));
        // ONNX emits exact [1,1,-1] -> Hold; native differs by one float32 ULP at Buy.
        // Every numerical difference passes ML tolerance, but the chosen action must still match exactly.
        Assert.Equal(RlAction.Hold, RlContract.SelectAction(new[] { 1f, 1f, -1f }));
        Assert.Equal(RlAction.Buy, RlContract.SelectAction(new[] { 1f, MathF.BitIncrement(1f), -1f }));
        Assert.Throws<InvalidDataException>(() => QValueService.ValidateParity(candidate.Directory));
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(candidate.Directory));
    }

    [Fact]
    public async Task Infer_UsesRealQOutputsAndExactAccountScalerSourceIdentity()
    {
        var candidate = Candidate();
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        using var engine = new SessionOwner(candidate.Source);
        var account = engine.Session.Reset();
        float[] observation = RlObservationBuilder.Build(account, candidate.Scaler);
        var service = new QValueService(registry);
        var q = service.Infer(generation.ModelId, observation, account, candidate.Source.Revision, Symbol, "immutable-request");
        Assert.Equal(new[] { 0f, 1f, -1f }, q.QValues);
        Assert.Equal(RlAction.Buy, q.SelectedAction);
        Assert.Equal(generation.ModelId, q.ModelId);
        Assert.Equal(account, q.Account);
        observation[0] += 1f;
        Assert.Throws<InvalidDataException>(() => service.Infer(generation.ModelId, observation, account, candidate.Source.Revision, Symbol, "stale"));
        Assert.Throws<InvalidDataException>(() => service.Infer(generation.ModelId, RlObservationBuilder.Build(account, candidate.Scaler), account, Revision, Symbol, "foreign-source"));
        Assert.Throws<InvalidDataException>(() => service.Infer(generation.ModelId, RlObservationBuilder.Build(account, candidate.Scaler), account, candidate.Source.Revision, "OTHER", "foreign-symbol"));
        var invented = account with { AccountFields = account.AccountFields.SetItem(2, 0m) };
        Assert.Throws<InvalidDataException>(() => service.Infer(generation.ModelId, RlObservationBuilder.Build(invented, candidate.Scaler), invented, candidate.Source.Revision, Symbol, "invented"));
    }

    [Theory]
    [InlineData(false, "output_name")] [InlineData(true, "output_name")]
    [InlineData(false, "input_name")] [InlineData(true, "input_name")]
    [InlineData(false, "output_dtype")] [InlineData(true, "output_dtype")]
    [InlineData(false, "input_dtype")] [InlineData(true, "input_dtype")]
    [InlineData(false, "output_shape")] [InlineData(true, "output_shape")]
    [InlineData(false, "input_shape")] [InlineData(true, "input_shape")]
    public void R05MetadataDiagnosticsIdentifyTheActualOutputContract(bool continuous, string mutation)
    {
        string path = DiagnosticModel(continuous, mutation);
        var error = Assert.Throws<InvalidDataException>(() => new RlQModel(path, continuous));
        Assert.Contains($"{RlContract.OnnxInputName}[1,{RlContract.ObservationLength}]", error.Message);
        AssertOutputDiagnostic(error.Message, continuous);
        Assert.Contains("float32", error.Message);
    }

    [Theory]
    [InlineData(false, "empty")] [InlineData(true, "empty")]
    [InlineData(false, "unpaired")] [InlineData(true, "unpaired")]
    [InlineData(false, "shape")] [InlineData(true, "shape")]
    [InlineData(false, "values")] [InlineData(true, "values")]
    public void R05ParityDiagnosticsDistinguishActorComponentsFromQActions(bool continuous, string mutation)
    {
        string modelPath = DiagnosticModel(continuous);
        using var model = new RlQModel(modelPath, continuous);
        float[][] observations = mutation == "empty" ? Array.Empty<float[]>() : new[] { new float[RlContract.ObservationLength] };
        float[] reference = continuous ? new[] { .25f, -.5f } : new[] { 0f, 1f, -1f };
        if (mutation == "shape") reference = reference.Concat(new[] { 0f }).ToArray();
        if (mutation == "values") reference[0] += .125f;
        string parityPath = Path.Combine(Path.GetDirectoryName(modelPath)!, RlFiles.Parity);
        Write(parityPath, new Dictionary<string, object>
        {
            ["observations"] = observations,
            [continuous ? RlContinuousContract.ParityField : RlContract.OnnxOutputName] = mutation is "empty" or "unpaired" ? Array.Empty<float[]>() : new[] { reference },
        });
        var error = Assert.Throws<InvalidDataException>(() => model.ValidateParity(parityPath));
        AssertOutputDiagnostic(error.Message, continuous);
        if (mutation == "values")
        {
            Assert.Contains("row 0", error.Message);
            Assert.Contains(continuous ? "component 0" : "action 0", error.Message);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void R05InferShapeDiagnosticsIdentifyTheActualOutputContract(bool continuous)
    {
        using var model = new RlQModel(DiagnosticModel(continuous, "runtime_shape"), continuous);
        var error = Assert.Throws<InvalidDataException>(() => model.Infer(Enumerable.Repeat(1f, RlContract.ObservationLength).ToArray()));
        AssertOutputDiagnostic(error.Message, continuous);
        Assert.Contains("shape", error.Message);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void R05ValidActorAndQInferenceAndParityKeepTheirOriginalValues(bool continuous)
    {
        string path = DiagnosticModel(continuous);
        using var model = new RlQModel(path, continuous);
        var observation = new float[RlContract.ObservationLength];
        float[] expected = continuous ? new[] { .25f, -.5f } : new[] { 0f, 1f, -1f };
        Assert.Equal(expected, model.Infer(observation));
        string parityPath = Path.Combine(Path.GetDirectoryName(path)!, RlFiles.Parity);
        Write(parityPath, new Dictionary<string, object> { ["observations"] = new[] { observation },
            [continuous ? RlContinuousContract.ParityField : RlContract.OnnxOutputName] = new[] { expected } });
        model.ValidateParity(parityPath);
        if (!continuous) Assert.Equal(RlAction.Buy, RlContract.SelectAction(expected));
    }

    private static void AssertOutputDiagnostic(string message, bool continuous)
    {
        Assert.Contains(continuous ? "normalized_actions[1,2]" : "q_values[1,3]", message);
        if (continuous) { Assert.DoesNotContain("Q ", message); Assert.DoesNotContain(RlContract.OnnxOutputName, message); }
    }

    private string DiagnosticModel(bool continuous, string mutation = "")
    {
        // Actual CPU ONNX graphs isolate tensor diagnostics without claiming trained-market qualification.
        string directory = Path.Combine(_root, "diagnostic_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, RlFiles.Model);
        var metadata = RlObservationBuilder.CreateMetadata(Symbol, Revision, Revision);
        if (continuous)
        {
            metadata["rl_contract_hash"] = RlContinuousContract.ContractHash; metadata["target_type"] = RlContinuousContract.Target;
            metadata["frozen_direction_model_id"] = Revision; metadata["frozen_direction_sha256"] = Revision;
        }
        string outputName = mutation == "output_name" ? "wrong_output" : continuous ? RlContinuousContract.OutputName : RlContract.OnnxOutputName;
        int count = (continuous ? RlContinuousContract.ActionCount : RlContract.ActionCount) + (mutation == "output_shape" ? 1 : 0);
        int inputCount = RlContract.ObservationLength + (mutation == "input_shape" ? 1 : 0);
        // ONNX protobuf field numbers and enum values follow the installed ONNX schema; these are fixture encoding, not product definitions.
        const int floatType = 1, doubleType = 11, int64Type = 7, irVersion = 8, opsetVersion = 13;
        int outputType = mutation == "output_dtype" ? doubleType : floatType;
        byte[] Message(Action<Stream> write) { using var stream = new MemoryStream(); write(stream); return stream.ToArray(); }
        void Bytes(Stream stream, int field, byte[] value) { WriteVarint(stream, (field << 3) | 2); WriteVarint(stream, value.Length); stream.Write(value); }
        void Number(Stream stream, int field, int value) { WriteVarint(stream, field << 3); WriteVarint(stream, value); }
        byte[] Tensor(string name, int type, int[] dimensions, byte[] data) => Message(s =>
        { foreach (int d in dimensions) Number(s, 1, d); Number(s, 2, type); WriteProtoString(s, 8, name); Bytes(s, 9, data); });
        byte[] Value(string name, int type, params int[] dimensions) => Message(s =>
        {
            WriteProtoString(s, 1, name); Bytes(s, 2, Message(t => Bytes(t, 1, Message(tt =>
            { Number(tt, 1, type); Bytes(tt, 2, Message(shape => { foreach (int d in dimensions) Bytes(shape, 1, Message(dim => Number(dim, 1, d))); })); }))));
        });
        byte[] Node(string op, string[] inputs, string output) => Message(s =>
        { foreach (string input in inputs) WriteProtoString(s, 1, input); WriteProtoString(s, 2, output); WriteProtoString(s, 4, op); });
        var values = continuous ? new[] { .25f, -.5f } : new[] { 0f, 1f, -1f };
        byte[] graph = Message(g =>
        {
            WriteProtoString(g, 2, "R05_numerical_diagnostic");
            if (mutation == "runtime_shape")
            {
                // Compress has a runtime-dependent output length while its declared ONNX output contract stays fixed.
                Bytes(g, 5, Tensor("axes", int64Type, new[] { 1 }, BitConverter.GetBytes(0L)));
                Bytes(g, 5, Tensor("zero", floatType, Array.Empty<int>(), BitConverter.GetBytes(0f)));
                Bytes(g, 1, Node("Squeeze", new[] { RlContract.OnnxInputName, "axes" }, "flat"));
                Bytes(g, 1, Node("Greater", new[] { "flat", "zero" }, "condition"));
                Bytes(g, 1, Node("Compress", new[] { "flat", "condition" }, "compressed"));
                Bytes(g, 1, Node("Unsqueeze", new[] { "compressed", "axes" }, outputName));
            }
            else Bytes(g, 5, Tensor(outputName, outputType, new[] { 1, count }, Enumerable.Range(0, count)
                .SelectMany(i => outputType == doubleType ? BitConverter.GetBytes((double)values[i % values.Length]) : BitConverter.GetBytes(values[i % values.Length])).ToArray()));
            Bytes(g, 11, Value(mutation == "input_name" ? "wrong_input" : RlContract.OnnxInputName, mutation == "input_dtype" ? doubleType : floatType, 1, inputCount));
            Bytes(g, 12, Value(outputName, outputType, 1, count));
        });
        File.WriteAllBytes(path, Message(model =>
        {
            Number(model, 1, irVersion); Bytes(model, 7, graph); Bytes(model, 8, Message(op => Number(op, 2, opsetVersion)));
            foreach (var pair in metadata) Bytes(model, 14, Message(entry => { WriteProtoString(entry, 1, pair.Key); WriteProtoString(entry, 2, pair.Value); }));
        }));
        Write(path + RlFiles.ScalerSuffix, new FixedScaler(FixedScaler.CurrentSchemaVersion, RlFiles.FeatureMode, RlContract.WindowLength,
            Array.Empty<int>(), null, FixedScaler.ChannelNames(metadata, Array.Empty<int>()),
            Enumerable.Repeat(new FixedScalerStatistic(100d, 1d, 0d, 1_000d), RlContract.MarketChannelCount).ToArray(), FixedScaler.ComputeContractHash(metadata)));
        return path;
    }

    [Fact]
    public async Task Replay_UsesCoreAccountAndPolicyCursorAndRejectsCancelledOrEndedCursor()
    {
        var candidate = Candidate();
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry);
        var block = candidate.Source.Catalog.Validation[0];
        var direction = await service.ReplayAsync(new RlReplayRequest(generation.ModelId, candidate.Directory, block.Id, 2, RlFiles.Target));
        Assert.Equal(1m, direction.Account.Quantity);
        Assert.Equal(2, direction.Account.HeldBars);
        var hold = await service.ReplayAsync(new RlReplayRequest(generation.ModelId, candidate.Directory, block.Id, 2, RlContract.HoldBaselineId));
        Assert.Equal(0m, hold.Account.Quantity);
        Assert.NotEqual(direction.RequestKey, hold.RequestKey);
        var sma = await service.ReplayAsync(new RlReplayRequest(generation.ModelId, candidate.Directory, block.Id, 2, RlContract.SmaBaselineId));
        Assert.Equal(0m, sma.Account.Quantity);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReplayAsync(new RlReplayRequest(generation.ModelId, candidate.Directory, block.Id, RlContract.EpisodeHorizon, RlFiles.Target)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReplayAsync(new RlReplayRequest(generation.ModelId, candidate.Directory, block.Id, 2, RlFiles.Target), cancelled.Token));
    }

    [Theory]
    [InlineData(RlFiles.Native)]
    [InlineData(RlFiles.RawParquet)]
    [InlineData(RlFiles.MetadataParquet)]
    public async Task CorruptRequiredFile_PreventsAcquisitionAndActivation(string file)
    {
        var candidate = Candidate();
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        using (var lease = registry.AcquireRl(generation.ModelId))
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(lease!.ModelPath)!, file), "changed");
        Assert.Throws<InvalidDataException>(() => registry.AcquireRl(generation.ModelId));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.ActivateRlAsync(generation.ModelId));
        Assert.Null(registry.RlActiveId);
    }

    [Fact]
    public void RehashedPreparedBarsCannotReplaceCopiedLocalPrices()
    {
        var candidate = Candidate();
        Change(candidate.Directory, RlFiles.Bars, root => root[0]!["close"] = 101m);
        Change(candidate.Directory, RlFiles.SourceManifest, root => root["prepared_file_sha256"] = ContentHash.OfFile(Path.Combine(candidate.Directory, RlFiles.Bars)));
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(candidate.Directory, Limits));
        Assert.Contains("copied local Parquet ingress", error.Message);
    }

    [Fact]
    public void RehashedMetadataCannotChangeDeclaredCurrency()
    {
        var candidate = Candidate();
        string path = Path.Combine(candidate.Directory, RlFiles.MetadataParquet);
        File.Delete(path);
        CreateParquet(path, "SELECT 'JPY' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        Change(candidate.Directory, RlFiles.SourceManifest, root => root["metadata_file_sha256"] = ContentHash.OfFile(path));
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(candidate.Directory, Limits));
        Assert.Contains("copied stored values", error.Message);
    }

    [Fact]
    public void UnverifiedCorporateActionHistoryCannotBeClaimedVerified()
    {
        var candidate = Candidate();
        Change(candidate.Directory, RlFiles.SourceManifest, root => root["corporate_action_coverage_status"] = "verified");
        Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(candidate.Directory, Limits));
    }

    [Fact]
    public async Task RollbackFiveAndLiveLease_PreserveOwnedGenerationUntilLeaseReleased()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var first = await registry.RegisterRlAsync(Candidate().Directory); await registry.ActivateRlAsync(first.ModelId);
        var lease = registry.AcquireRl(first.ModelId)!;
        for (int i = 0; i <= RlContract.RollbackGenerationCount; i++)
        {
            var next = await registry.RegisterRlAsync(Candidate(i / 100m).Directory); await registry.ActivateRlAsync(next.ModelId);
        }
        Assert.NotNull(registry.GetRlGeneration(first.ModelId));
        Assert.True(File.Exists(lease.ModelPath));
        lease.Dispose();
        var last = await registry.RegisterRlAsync(Candidate(0.5m).Directory); await registry.ActivateRlAsync(last.ModelId);
        Assert.Null(registry.GetRlGeneration(first.ModelId));
        // Active + five rollback ids + the oldest id needed by the independently recoverable backup.
        Assert.Equal(RlContract.RollbackGenerationCount + 2, registry.ListRl().Count);
    }

    [Theory]
    [InlineData("ETF")]
    [InlineData("CRYPTOCURRENCY")]
    [InlineData(null)]
    public void SourceRejectsAnUndeclaredOrNonEquityInstrument(string? instrument)
    {
        var candidate = Candidate();
        Change(candidate.Directory, RlFiles.SourceManifest, root => root["instrument_type"] = instrument);
        Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(candidate.Directory, Limits));
    }

    [Fact]
    public void UnknownHistoricalEvidenceCannotBeRelabeledAsKnownImputation()
    {
        var candidate = Candidate();
        Change(candidate.Directory, RlFiles.SourceManifest, root =>
        {
            root["ingestion_imputation_status"] = "known_imputed";
            root["known_imputed_dates"] = new JsonArray(DateOnly.FromDateTime(Start).ToString("yyyy-MM-dd"));
        });
        Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(candidate.Directory, Limits));
    }

    [Fact]
    public void OldYfinanceSnapshotCannotBecomeLocalSourceOrGeneration()
    {
        var candidate = Candidate();
        Change(candidate.Directory, RlFiles.SourceManifest, root => { root["provider"] = "yfinance"; root["provider_version"] = "0.2.66"; });
        Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(candidate.Directory, Limits));
    }

    [Fact]
    public void IpcRejectsDuplicateStepAndRetiredIdentityBeforeReapplyingOrders()
    {
        var candidate = Candidate();
        using var jobDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate.Directory, RlFiles.Job)));
        var costs = jobDocument.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        var runId = Guid.NewGuid(); string id = Guid.NewGuid().ToString("N");
        var metadata = RlObservationBuilder.CreateMetadata(Symbol, candidate.Source.Revision, Revision);
        using var host = new RlIpcHost(runId, candidate.Source, costs, candidate.Directory, metadata, null);
        JsonElement Frame(long sequence, string method, object payload, string session = "") => JsonSerializer.SerializeToElement(new
        { protocol_version = RlContract.SchemaVersion, run_id = runId.ToString("N"), contract_hash = RlContract.ContractHash,
            sequence, method, session_id = session, payload }, RlFiles.Json);
        host.Handle(Frame(1, "Ready", new { }), CancellationToken.None);
        host.Handle(Frame(2, "Reset", new { role = "train", block_id = candidate.Source.Catalog.Train[0].Id,
            seed = RlContract.CandidateSeeds[0], checkpoint = 0 }, id), CancellationToken.None);
        host.Handle(Frame(3, "Step", new { action = (int)RlAction.Buy }, id), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => host.Handle(Frame(3, "Step", new { action = (int)RlAction.Sell }, id), CancellationToken.None));
        var response = JsonSerializer.SerializeToElement(host.Handle(Frame(4, "Step", new { action = (int)RlAction.Hold }, id), CancellationToken.None), RlFiles.Json);
        Assert.Equal("1", response.GetProperty("result").GetProperty("info").GetProperty("account").GetProperty("quantity").GetString());
        Assert.Equal(2, host.Transitions);
        host.Handle(Frame(5, "Dispose", new { }, id), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => host.Handle(Frame(6, "Reset", new { role = "train", block_id = candidate.Source.Catalog.Train[0].Id,
            seed = RlContract.CandidateSeeds[0], checkpoint = 0 }, id), CancellationToken.None));
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static void CreateParquet(string path, string query)
    {
        using var db = new DuckDBConnection("DataSource=:memory:"); db.Open(); using var command = db.CreateCommand();
        command.CommandText = "SET threads=" + RlContract.CpuThreads.ToString(CultureInfo.InvariantCulture); command.ExecuteNonQuery();
        command.CommandText = $"COPY ({query}) TO '{ParquetMarketDataProvider.EscapeDuckDbPath(path.Replace('\\','/'))}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }
    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, RlFiles.Json));
    private static void Change(string directory, string file, Action<JsonNode> mutation)
    {
        string path = Path.Combine(directory, file); var root = JsonNode.Parse(File.ReadAllText(path))!; mutation(root); File.WriteAllText(path, root.ToJsonString());
    }
    private static void WriteProtoString(Stream stream, int field, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); WriteVarint(stream, (field << 3) | 2); WriteVarint(stream, bytes.Length); stream.Write(bytes); }
    private static void WriteVarint(Stream stream, int value)
    { uint remaining = checked((uint)value); while (remaining >= 128) { stream.WriteByte((byte)((remaining & 127) | 128)); remaining >>= 7; } stream.WriteByte((byte)remaining); }
    private sealed class SessionOwner : IDisposable
    {
        public IRlTradingSession Session { get; }
        public SessionOwner(RlSourceSnapshot source)
        {
            var costs = RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 1_000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m });
            Session = new BacktestEngine(new IndicatorFactory()).CreateRlSession(source.CreateInput(source.Catalog.Validation[0]), costs);
        }
        public void Dispose() => Session.Dispose();
    }
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && Directory.Exists(target)) Directory.Delete(target, true);
    }
}
