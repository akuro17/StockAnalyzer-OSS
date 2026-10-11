using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The attention bands of a saved ensemble: the pure mixing rule (hand-computed vectors), the member selection and caching of the service, and
/// parity with the single-model attention through the real registry and the Python-written fixture.
/// </summary>
public sealed class EnsembleAttentionServiceTests : IDisposable
{
    private const double Tolerance = 1e-6;
    private static readonly DateTime Day0 = new(2021, 3, 1);
    private static readonly string Fixture = Path.Combine("Assets", "attention_fixture.onnx");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_ens_attention_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestDirectory.Delete(_root);   // retried: a child process of another test can hold an inherited model handle for a moment
    }

    private static CandleData[] History(int count = 12) => Enumerable.Range(0, count)
        .Select(i => new CandleData(Day0.AddDays(i), 100m + i, 101m + i, 99m + i, 100.5m + i, 1000 + i)).ToArray();

    private static EnsembleSpec Spec(params (string Id, double Weight)[] members) =>
        new(Array.AsReadOnly(members.Select(m => new EnsembleMember(m.Id, m.Weight)).ToArray()));

    private static AttentionRequestKey KeyOf(EnsembleSpec spec, CandleData[] history) => new(spec.Fingerprint(), "SYM", "Daily", history[^1].Timestamp, 7);

    /// <summary>The attention of one member over its last <paramref name="weights"/>.Length bars of the history.</summary>
    private static AttentionSnapshot MemberSnapshot(AttentionRequestKey key, CandleData[] history, int heads, params float[] weights)
    {
        float max = weights.Max();
        return new AttentionSnapshot(key, heads, ImmutableArray.CreateRange(history.Skip(history.Length - weights.Length).Select(c => c.Timestamp)),
            ImmutableArray.Create(weights), ImmutableArray.CreateRange(weights.Select(w => w / max)));
    }

    // --- Fingerprint -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Fingerprint_IsStable_AndChangesWithAMemberAWeightOrTheOrder()
    {
        var spec = Spec(("a", 0.25), ("b", 0.75));
        Assert.StartsWith(EnsembleSpec.FingerprintPrefix, spec.Fingerprint());
        Assert.Equal(spec.Fingerprint(), Spec(("a", 0.25), ("b", 0.75)).Fingerprint());
        Assert.NotEqual(spec.Fingerprint(), Spec(("a", 0.5), ("b", 0.5)).Fingerprint());
        Assert.NotEqual(spec.Fingerprint(), Spec(("b", 0.75), ("a", 0.25)).Fingerprint());
        Assert.NotEqual(spec.Fingerprint(), Spec(("a", 0.25), ("c", 0.75)).Fingerprint());
    }

    // --- The mixing rule ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Combine_RightAlignsTheMembersAtTheAnchorBar_AndGivesOlderBarsZeroFromAShorterWindow()
    {
        var history = History();
        var spec = Spec(("a", 0.5), ("b", 0.5));
        var key = KeyOf(spec, history);
        var longWindow = MemberSnapshot(key, history, heads: 4, 0.1f, 0.2f, 0.3f, 0.4f);
        var shortWindow = MemberSnapshot(key, history, heads: 2, 0.6f, 0.4f);

        var result = EnsembleAttentionService.Combine(key, new[] { (0.5, longWindow), (0.5, shortWindow) }, memberCount: 2, totalWeight: 1.0);

        Assert.Equal(AttentionStatus.Ready, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Equal(key, snapshot.Key);
        Assert.Equal(longWindow.Timestamps, snapshot.Timestamps);          // the longest window spans the bands
        // bars (oldest -> newest): 0.5*[.1,.2,.3,.4] + 0.5*[0,0,.6,.4]
        double[] expected = { 0.05, 0.10, 0.45, 0.40 };
        for (int i = 0; i < expected.Length; i++) Assert.InRange(snapshot.Weights[i], expected[i] - Tolerance, expected[i] + Tolerance);
        for (int i = 0; i < expected.Length; i++) Assert.InRange(snapshot.Relative[i], expected[i] / 0.45 - Tolerance, expected[i] / 0.45 + Tolerance);
        Assert.Equal(1f, snapshot.Relative.Max());
        Assert.InRange(snapshot.Weights.Sum(), 1 - 1e-5, 1 + 1e-5);       // a convex combination of softmax rows still sums to 1
        Assert.Equal(4, snapshot.Heads);
        Assert.Equal(2, snapshot.CapableMembers);
        Assert.Equal(2, snapshot.MemberCount);
        Assert.True(snapshot.IsEnsemble);
    }

    [Fact]
    public void Combine_UnequalWeights_RenormaliseOverTheContributingMembers()
    {
        var history = History();
        var spec = Spec(("a", 0.5), ("b", 0.3), ("c", 0.2));
        var key = KeyOf(spec, history);
        // only a (0.5) and c (0.2) contribute: shares 5/7 and 2/7
        var a = MemberSnapshot(key, history, 1, 0.2f, 0.8f);
        var c = MemberSnapshot(key, history, 1, 0.9f, 0.1f);

        var snapshot = EnsembleAttentionService.Combine(key, new[] { (0.5, a), (0.2, c) }, memberCount: 3, totalWeight: 1.0).Snapshot!;

        Assert.InRange(snapshot.Weights[0], 5.0 / 7 * 0.2 + 2.0 / 7 * 0.9 - Tolerance, 5.0 / 7 * 0.2 + 2.0 / 7 * 0.9 + Tolerance);
        Assert.InRange(snapshot.Weights[1], 5.0 / 7 * 0.8 + 2.0 / 7 * 0.1 - Tolerance, 5.0 / 7 * 0.8 + 2.0 / 7 * 0.1 + Tolerance);
        Assert.Equal(2, snapshot.CapableMembers);
        Assert.Equal(3, snapshot.MemberCount);
    }

    [Fact]
    public void Combine_WithoutTotalWeight_MixesAsBefore_AndLeavesCoverageAtItsDefault()
    {
        var history = History();
        var spec = Spec(("a", 0.5), ("b", 0.3), ("c", 0.2));
        var key = KeyOf(spec, history);
        var a = MemberSnapshot(key, history, 3, 0.1f, 0.2f, 0.7f);
        var c = MemberSnapshot(key, history, 3, 0.3f, 0.3f, 0.4f);
        var contributors = new[] { (0.5, a), (0.2, c) };

        var withoutTotal = EnsembleAttentionService.Combine(key, contributors, memberCount: 3).Snapshot!;
        var withTotal = EnsembleAttentionService.Combine(key, contributors, memberCount: 3, totalWeight: 1.0).Snapshot!;

        Assert.Equal(0d, withoutTotal.CoverageWeight);
        Assert.Equal(withTotal.Weights, withoutTotal.Weights);
        Assert.Equal(withTotal.Relative, withoutTotal.Relative);
        Assert.Equal(withTotal.CapableMembers, withoutTotal.CapableMembers);
        Assert.Equal(withTotal.WindowsDiffer, withoutTotal.WindowsDiffer);
    }

    [Fact]
    public void Combine_OneContributorOfWeightOne_ReproducesItsOwnAttention()
    {
        var history = History();
        var spec = Spec(("a", 1.0));
        var key = KeyOf(spec, history);
        var only = MemberSnapshot(key, history, 3, 0.1f, 0.2f, 0.7f);
        var snapshot = EnsembleAttentionService.Combine(key, new[] { (1.0, only) }, memberCount: 1, totalWeight: 1.0).Snapshot!;
        Assert.Equal(only.Weights.Length, snapshot.Weights.Length);
        for (int i = 0; i < only.Weights.Length; i++)
        {
            Assert.InRange(snapshot.Weights[i], only.Weights[i] - Tolerance, only.Weights[i] + Tolerance);
            Assert.InRange(snapshot.Relative[i], only.Relative[i] - Tolerance, only.Relative[i] + Tolerance);
        }
    }

    [Fact]
    public void Combine_ReportsTheWeightCoverage_AndWhetherTheWindowsDiffer()
    {
        var history = History();
        var spec = Spec(("a", 0.5), ("b", 0.3), ("c", 0.2));
        var key = KeyOf(spec, history);
        var a = MemberSnapshot(key, history, 1, 0.1f, 0.2f, 0.3f, 0.4f);   // window 4
        var c = MemberSnapshot(key, history, 1, 0.6f, 0.4f);               // window 2
        var sameWindowAsA = MemberSnapshot(key, history, 1, 0.4f, 0.3f, 0.2f, 0.1f);

        var partial = EnsembleAttentionService.Combine(key, new[] { (0.5, a), (0.2, c) }, memberCount: 3, totalWeight: 1.0).Snapshot!;
        Assert.InRange(partial.CoverageWeight, 0.7 - 1e-9, 0.7 + 1e-9);        // b (0.3) does not contribute
        Assert.True(partial.WindowsDiffer);

        var equalWindows = EnsembleAttentionService.Combine(key, new[] { (0.5, a), (0.3, sameWindowAsA) }, memberCount: 3, totalWeight: 1.0).Snapshot!;
        Assert.InRange(equalWindows.CoverageWeight, 0.8 - 1e-9, 0.8 + 1e-9);
        Assert.False(equalWindows.WindowsDiffer);

        var all = EnsembleAttentionService.Combine(key, new[] { (0.5, a), (0.3, sameWindowAsA), (0.2, c) }, memberCount: 3, totalWeight: 1.0).Snapshot!;
        Assert.InRange(all.CoverageWeight, 1.0 - 1e-9, 1.0 + 1e-9);
    }

    [Fact]
    public async Task Compute_PassesTheWeightOfEveryMember_AsTheCoverageDenominator()
    {
        var history = History();
        var spec = Spec(("a", 0.6), ("b", 0.4), ("z", 0.0));   // z has weight 0: it never contributes but is part of the denominator sum (1.0)
        var key = KeyOf(spec, history);
        var members = Members(k => k.ModelId == "a"
            ? AttentionResult.Ready(MemberSnapshot(k, history, 1, 0.3f, 0.7f)) : AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar));

        var snapshot = (await new EnsembleAttentionService(members.Object).ComputeAsync(spec, key, history)).Snapshot!;

        Assert.InRange(snapshot.CoverageWeight, 0.6 - 1e-9, 0.6 + 1e-9);
        Assert.False(snapshot.WindowsDiffer);
    }

    [Fact]
    public void Combine_MembersThatDoNotEndAtTheSameBar_IsAFailure()
    {
        var history = History();
        var spec = Spec(("a", 0.5), ("b", 0.5));
        var key = KeyOf(spec, history);
        var a = MemberSnapshot(key, history, 1, 0.5f, 0.5f);
        var shifted = MemberSnapshot(key, history.Take(history.Length - 1).ToArray(), 1, 0.5f, 0.5f);
        Assert.Equal(AttentionStatus.Failed, EnsembleAttentionService.Combine(key, new[] { (0.5, a), (0.5, shifted) }, 2, 1.0).Status);
    }

    // --- Member selection, caching, cancellation ---------------------------------------------------------------------------------------------

    private static Mock<IAttentionService> Members(Func<AttentionRequestKey, AttentionResult> answer)
    {
        var members = new Mock<IAttentionService>(MockBehavior.Strict);
        members.Setup(m => m.ComputeMemberAsync(It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .Returns<AttentionRequestKey, IReadOnlyList<CandleData>, CancellationToken>((key, _, _) => Task.FromResult(answer(key)));
        return members;
    }

    [Fact]
    public async Task Compute_SkipsMembersWithoutAttention_AndRenormalisesTheRest()
    {
        var history = History();
        var spec = Spec(("a", 0.5), ("b", 0.3), ("c", 0.2));
        var key = KeyOf(spec, history);
        var members = Members(k => k.ModelId switch
        {
            "a" => AttentionResult.Ready(MemberSnapshot(k, history, 2, 0.2f, 0.8f)),
            "b" => AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar),
            _ => AttentionResult.Ready(MemberSnapshot(k, history, 2, 0.9f, 0.1f)),
        });

        var result = await new EnsembleAttentionService(members.Object).ComputeAsync(spec, key, history);

        Assert.Equal(AttentionStatus.Ready, result.Status);
        Assert.Equal(2, result.Snapshot!.CapableMembers);
        Assert.Equal(3, result.Snapshot.MemberCount);
        Assert.InRange(result.Snapshot.Weights[0], 5.0 / 7 * 0.2 + 2.0 / 7 * 0.9 - Tolerance, 5.0 / 7 * 0.2 + 2.0 / 7 * 0.9 + Tolerance);
        Assert.Equal(key, result.Snapshot.Key);                       // published under the ensemble key, not a member key
        members.Verify(m => m.ComputeMemberAsync(It.Is<AttentionRequestKey>(k => k.ModelId == "a" && k.Symbol == "SYM" && k.DataRevision == 7),
            It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Compute_AMemberOfWeightZero_IsNeverComputed()
    {
        var history = History();
        var spec = Spec(("a", 1.0), ("b", 0.0));
        var key = KeyOf(spec, history);
        var members = Members(k => AttentionResult.Ready(MemberSnapshot(k, history, 1, 0.4f, 0.6f)));

        var result = await new EnsembleAttentionService(members.Object).ComputeAsync(spec, key, history);

        Assert.Equal(1, result.Snapshot!.CapableMembers);
        Assert.Equal(2, result.Snapshot.MemberCount);
        members.Verify(m => m.ComputeMemberAsync(It.Is<AttentionRequestKey>(k => k.ModelId == "b"), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Compute_NoMemberWithAttention_IsUnsupported_AndAnUnusableSidecarIsAFailure()
    {
        var history = History();
        var spec = Spec(("a", 0.5), ("b", 0.5));
        var key = KeyOf(spec, history);

        var none = await new EnsembleAttentionService(Members(_ => AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar)).Object).ComputeAsync(spec, key, history);
        Assert.Equal(AttentionStatus.Unsupported, none.Status);
        Assert.Equal(AttentionContract.UnsupportedReason.NoAttentionMember, none.Reason);

        var broken = await new EnsembleAttentionService(Members(k => k.ModelId == "a"
            ? AttentionResult.Failed("corrupt sidecar") : AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar)).Object).ComputeAsync(spec, key, history);
        Assert.Equal(AttentionStatus.Failed, broken.Status);
        Assert.Equal("corrupt sidecar", broken.Detail);
    }

    [Fact]
    public async Task Compute_ReusesAResultForTheSameKey_ButNotAFailure_AndNotForAnotherRevision()
    {
        var history = History();
        var spec = Spec(("a", 1.0));
        var key = KeyOf(spec, history);
        int calls = 0;
        var members = Members(k => { calls++; return AttentionResult.Ready(MemberSnapshot(k, history, 1, 0.3f, 0.7f)); });
        var service = new EnsembleAttentionService(members.Object);

        var first = await service.ComputeAsync(spec, key, history);
        var second = await service.ComputeAsync(spec, key, history);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        await service.ComputeAsync(spec, key with { DataRevision = 8 }, history);
        Assert.Equal(2, calls);

        int failures = 0;
        var flaky = new EnsembleAttentionService(Members(_ => { failures++; return AttentionResult.Failed("transient"); }).Object);
        await flaky.ComputeAsync(spec, key, history);
        await flaky.ComputeAsync(spec, key, history);
        Assert.Equal(2, failures);
    }

    // --- The history-content key (SA-ATT-HIST-v1) --------------------------------------------------------------------------------------------

    private static readonly CandleData[] VectorHistory =
    {
        new(new DateTime(2021, 3, 1), 100.5m, 101m, 99.5m, 100.75m, 1000),
        new(new DateTime(2021, 3, 2), 100.75m, 102m, 100m, 101.25m, 1500),
    };

    [Fact]
    public void HistoryHash_MatchesTheKnownAnswerVectors()
    {
        Assert.Equal("3378880c578bfcfaf7e96325ad353f6c6ba43aa47d088918aec7a9691f269f35", EnsembleAttentionService.ComputeHistoryHash(Array.Empty<CandleData>()));
        Assert.Equal("f8f3deca4efd826ed615e77a54c9fb459e78c6fd22cd87819edaeacd6dedf9a5", EnsembleAttentionService.ComputeHistoryHash(VectorHistory));
    }

    /// <summary>Counts the member computations of one single-member ensemble over <paramref name="first"/> and then <paramref name="second"/> under one key.</summary>
    private static async Task<(int Calls, AttentionResult First, AttentionResult Second)> ComputeTwice(CandleData[] first, CandleData[] second)
    {
        var spec = Spec(("a", 1.0));
        var key = KeyOf(spec, first);
        int calls = 0;
        var members = Members(k => { calls++; return AttentionResult.Ready(MemberSnapshot(k, first, 1, 0.3f, 0.7f)); });
        var service = new EnsembleAttentionService(members.Object);
        var one = await service.ComputeAsync(spec, key, first);
        var two = await service.ComputeAsync(spec, key, second);
        return (calls, one, two);
    }

    [Fact]
    public async Task Compute_SameKeyAndIdenticalHistory_IsAHit()
    {
        var (calls, first, second) = await ComputeTwice(History(), History());
        Assert.Equal(1, calls);
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("last-close")]
    [InlineData("last-volume")]
    [InlineData("first-open")]
    [InlineData("scale")]
    public async Task Compute_SameKeyButDifferentHistoryContent_IsAMiss(string change)
    {
        var changed = History();
        int last = changed.Length - 1;
        switch (change)
        {
            case "last-close": changed[last] = changed[last] with { Close = changed[last].Close + 0.01m }; break;
            case "last-volume": changed[last] = changed[last] with { Volume = changed[last].Volume + 1 }; break;
            case "first-open": changed[0] = changed[0] with { Open = changed[0].Open + 1m }; break;
            default: changed[0] = changed[0] with { High = 101.00m }; break;   // History() has High 101m: the same number with another scale
        }

        var (calls, first, second) = await ComputeTwice(History(), changed);

        Assert.Equal(2, calls);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Compute_AKeyOfAnotherEnsemble_IsRefused_AndACancelledTokenThrows()
    {
        var history = History();
        var spec = Spec(("a", 1.0));
        var service = new EnsembleAttentionService(Members(k => AttentionResult.Ready(MemberSnapshot(k, history, 1, 0.3f, 0.7f))).Object);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ComputeAsync(spec, KeyOf(Spec(("z", 1.0)), history), history));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ComputeAsync(spec, KeyOf(spec, history), history, cancelled.Token));
    }

    // --- The real thing: registry + Python-written fixture ----------------------------------------------------------------------------------

    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private string Source()
    {
        var directory = Path.Combine(_root, "source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, "attention_fixture.onnx");
        File.Copy(Fixture, model);
        File.Copy(Fixture + AttentionContract.SidecarSuffix, model + AttentionContract.SidecarSuffix);
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var report = new
        {
            evaluation_status = "independent_outer", evaluation_revision = Revision, scored_model_sha256 = hash,
            target_type = metadata[PredictionModelMetadata.TargetTypeKey], timeframe = "daily",
            horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]), class_order = metadata[PredictionModelMetadata.ClassOrderKey],
            fold_macro_f1 = 0.4, fold_is_holdout = 1.0, fold = 2.0, fold_n = 20.0, n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = Revision, scored_model_sha256 = hash, fold_macro_f1 = 0.2 },
                new { fold = 1.0, evaluation_revision = Revision, scored_model_sha256 = hash, fold_macro_f1 = 0.3 },
                new { fold = 2.0, evaluation_revision = Revision, scored_model_sha256 = hash, fold_macro_f1 = 0.4 },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return model;
    }

    private static CandleData[] ParityCandles()
    {
        using var parity = AttentionContractTests.Parity();
        var item = parity.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "newest_bar_of_a_short_history");
        return item.GetProperty("candles").EnumerateArray().Select(row =>
        {
            var c = row.EnumerateArray().ToArray();
            return new CandleData(DateTime.ParseExact(c[0].GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), (decimal)c[1].GetDouble(),
                (decimal)c[2].GetDouble(), (decimal)c[3].GetDouble(), (decimal)c[4].GetDouble(), c[5].GetInt64());
        }).ToArray();
    }

    [Fact]
    public async Task OneMemberOfWeightOne_ReproducesTheSingleModelAttention_ThroughTheRealRegistry()
    {
        var candles = ParityCandles();
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var manifest = await registry.RegisterAsync(Source());
        var attention = new AttentionService(registry, new PredictionServiceTests.TestSettings(predictionWindowSize: 8), new MLDataProcessor());
        var spec = Spec((manifest.ModelId, 1.0));

        var single = (await attention.ComputeAsync(new AttentionRequestKey(manifest.ModelId, "SYM", "Daily", candles[^1].Timestamp, 1), candles)).Snapshot!;
        var ensemble = (await new EnsembleAttentionService(attention).ComputeAsync(spec, KeyOf(spec, candles), candles)).Snapshot!;

        Assert.Equal(single.Timestamps, ensemble.Timestamps);
        Assert.Equal(single.Heads, ensemble.Heads);
        for (int i = 0; i < single.Weights.Length; i++)
        {
            Assert.InRange(ensemble.Weights[i], single.Weights[i] - 1e-6, single.Weights[i] + 1e-6);
            Assert.InRange(ensemble.Relative[i], single.Relative[i] - 1e-6, single.Relative[i] + 1e-6);
        }

        Assert.Equal(1, ensemble.CapableMembers);
        Assert.Equal(1, ensemble.MemberCount);
    }

    [Fact]
    public async Task AMemberIsReadUnderItsOwnContract_NotTheConfiguredSingleModelWindow()
    {
        var candles = ParityCandles();
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var manifest = await registry.RegisterAsync(Source());
        var key = new AttentionRequestKey(manifest.ModelId, "SYM", "Daily", candles[^1].Timestamp, 1);
        var configuredElsewhere = new AttentionService(registry, new PredictionServiceTests.TestSettings(predictionWindowSize: 9), new MLDataProcessor());

        var asSingle = await configuredElsewhere.ComputeAsync(key, candles);
        var asMember = await configuredElsewhere.ComputeMemberAsync(key, candles);
        var matching = await new AttentionService(registry, new PredictionServiceTests.TestSettings(predictionWindowSize: 8), new MLDataProcessor()).ComputeAsync(key, candles);

        Assert.Equal(AttentionStatus.Unsupported, asSingle.Status);        // the single path still requires the configured window
        Assert.Equal(AttentionStatus.Ready, asMember.Status);               // a member brings its own window (8) from its metadata
        Assert.Equal(matching.Snapshot!.Weights, asMember.Snapshot!.Weights);
    }

    [Fact]
    public async Task AGenerationWithoutASidecar_IsNotACapableMember_AndAnAllIncapableEnsembleIsUnsupported()
    {
        var candles = ParityCandles();
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var source = Source();
        File.Delete(source + AttentionContract.SidecarSuffix);
        var manifest = await registry.RegisterAsync(source);
        var attention = new AttentionService(registry, new PredictionServiceTests.TestSettings(predictionWindowSize: 8), new MLDataProcessor());
        var spec = Spec((manifest.ModelId, 1.0));

        var result = await new EnsembleAttentionService(attention).ComputeAsync(spec, KeyOf(spec, candles), candles);

        Assert.Equal(AttentionStatus.Unsupported, result.Status);
        Assert.Equal(AttentionContract.UnsupportedReason.NoAttentionMember, result.Reason);
    }

    [Fact]
    public void ReadOwnFeatureContract_ReadsTheModelsOwnModeAndWindow_AndRefusesMissingKeys()
    {
        using var session = new InferenceSession(Fixture);
        var (mode, window, spec) = PredictionModelMetadata.ReadOwnFeatureContract(session.ModelMetadata.CustomMetadataMap);
        Assert.Equal(8, window);
        Assert.Equal(PredictionModelMetadata.ParseFeatureMode(session.ModelMetadata.CustomMetadataMap[PredictionModelMetadata.FeatureModeKey]), mode);
        Assert.Equal(mode == PredictionFeatureMode.ComposedFeatures, spec is not null);

        var withoutWindow = new Dictionary<string, string>(session.ModelMetadata.CustomMetadataMap);
        withoutWindow.Remove(PredictionModelMetadata.WindowSizeKey);
        Assert.Throws<InvalidDataException>(() => PredictionModelMetadata.ReadOwnFeatureContract(withoutWindow));
        Assert.Throws<InvalidDataException>(() => PredictionModelMetadata.ReadOwnFeatureContract(new Dictionary<string, string>()));
    }
}
