#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlPortfolioArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_artifacts_" + Guid.NewGuid().ToString("N"));
    private static readonly string Universe = new('a', 64), Calendar = new('b', 64);
    private static readonly DateTimeOffset Fixed = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private RlPortfolioArtifactStore Store(DateTimeOffset? now = null) => new(_root, new ManualTimeProvider(now ?? Fixed));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // Independent of the code under test: lowercase SHA-256 of the exact bytes.
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Manifest(string directory) => Path.Combine(directory, RlPortfolioContract.ArtifactManifest);

    private async Task<(RlPortfolioArtifactStore Store, Guid Id, string Directory)> Published(DateTimeOffset? now = null)
    {
        var store = Store(now); var id = Guid.NewGuid(); string directory = store.Begin(id);
        File.WriteAllText(Path.Combine(directory, "report.json"), "{\"a\":1}"); File.WriteAllText(Path.Combine(directory, "weights.bin"), "weights");
        await store.PublishAsync(id, Universe, Calendar, new[] { "weights.bin", "report.json" });
        return (store, id, directory);
    }

    [Fact]
    public async Task PublishWritesTheManifestLastWithIndependentlyComputedHashesUnderTheDataFolder()
    {
        var (store, id, directory) = await Published();
        Assert.Equal(Path.Combine(_root, "TrainingArtifacts", "RLPortfolio", id.ToString("N")), directory);
        var entry = Assert.Single(store.List());
        Assert.Equal(RlPortfolioArtifactStatus.Verified, entry.Status); Assert.Equal(id, entry.RunId); Assert.Null(entry.Problem);
        var manifest = entry.Manifest!;
        Assert.Equal((1, "SA-RL-PORTFOLIO-v1", Fixed), (manifest.SchemaVersion, manifest.SpecificationId, manifest.CreatedUtc));
        Assert.Equal((Universe, Calendar), (manifest.UniverseSha256, manifest.CalendarSha256));
        Assert.Equal(new[] { "report.json", "weights.bin" }, manifest.Files.Keys);
        Assert.Equal(Sha("{\"a\":1}"), manifest.Files["report.json"]); Assert.Equal(Sha("weights"), manifest.Files["weights.bin"]);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task ARunSurvivesANewStoreInstanceAndCannotBeBegunOrPublishedTwice()
    {
        var (_, id, _) = await Published();
        var reopened = Store();
        Assert.Equal(RlPortfolioArtifactStatus.Verified, Assert.Single(reopened.List()).Status);
        Assert.Throws<InvalidOperationException>(() => reopened.Begin(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.PublishAsync(id, Universe, Calendar, new[] { "report.json" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.PublishAsync(Guid.NewGuid(), Universe, Calendar, new[] { "report.json" }));
    }

    [Fact]
    public async Task TamperingMissingFilesAndBrokenManifestsAreReportedNeverHidden()
    {
        foreach (var mutation in new (string Name, Action<string> Change, RlPortfolioArtifactStatus Expected)[]
        {
            ("changed", d => File.AppendAllText(Path.Combine(d, "weights.bin"), "x"), RlPortfolioArtifactStatus.Corrupt),
            ("deleted", d => File.Delete(Path.Combine(d, "report.json")), RlPortfolioArtifactStatus.Corrupt),
            ("no manifest", d => File.Delete(Manifest(d)), RlPortfolioArtifactStatus.Incomplete),
            ("garbage", d => File.WriteAllText(Manifest(d), "{"), RlPortfolioArtifactStatus.Invalid),
            ("wrong schema", d => Rewrite(d, m => m["schema_version"] = 2), RlPortfolioArtifactStatus.Invalid),
            ("wrong spec", d => Rewrite(d, m => m["specification_id"] = "SA-RL-PORTFOLIO-v2"), RlPortfolioArtifactStatus.Invalid),
            ("other run id", d => Rewrite(d, m => m["run_id"] = Guid.NewGuid().ToString()), RlPortfolioArtifactStatus.Invalid),
            ("bad hash", d => Rewrite(d, m => m["universe_sha256"] = "ABC"), RlPortfolioArtifactStatus.Invalid),
            ("path in file name", d => Rewrite(d, m => m["files"] = new System.Text.Json.Nodes.JsonObject { ["../x"] = Sha("x") }), RlPortfolioArtifactStatus.Invalid),
            ("empty files", d => Rewrite(d, m => m["files"] = new System.Text.Json.Nodes.JsonObject()), RlPortfolioArtifactStatus.Invalid),
        })
        {
            var (store, _, directory) = await Published();
            mutation.Change(directory);
            var entry = store.List().Single(e => e.Directory == directory);
            Assert.True(entry.Status == mutation.Expected, mutation.Name + ": " + entry.Status);
            if (mutation.Expected == RlPortfolioArtifactStatus.Corrupt) Assert.NotNull(entry.Problem);
        }
    }

    private static void Rewrite(string directory, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Manifest(directory)))!.AsObject(); change(node);
        File.WriteAllText(Manifest(directory), node.ToJsonString());
    }

    [Fact]
    public async Task ListingOrdersVerifiedRunsNewestFirstAndIgnoresForeignFolders()
    {
        var older = await Published(Fixed); var newer = await Published(Fixed.AddHours(1));
        var store = Store(); store.Begin(Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(store.Root, "not-a-run"));
        var list = store.List();
        Assert.Equal(3, list.Length);
        Assert.Equal(new[] { newer.Id, older.Id }, list.Take(2).Select(e => e.RunId));
        Assert.Equal(RlPortfolioArtifactStatus.Incomplete, list[2].Status);
        Assert.Empty(Store().List().Where(e => e.Status == RlPortfolioArtifactStatus.Invalid));
        Assert.Empty(new RlPortfolioArtifactStore(Path.Combine(_root, "elsewhere")).List());
    }

    [Fact]
    public async Task PublishRejectsUnsafeInputsBeforeWritingAnything()
    {
        var store = Store(); var id = Guid.NewGuid(); string directory = store.Begin(id);
        File.WriteAllText(Path.Combine(directory, "report.json"), "r"); Directory.CreateDirectory(Path.Combine(directory, "sub"));
        async Task Rejected<T>(string universe, string[] files) where T : Exception
        {
            await Assert.ThrowsAsync<T>(() => store.PublishAsync(id, universe, Calendar, files));
            Assert.False(File.Exists(Manifest(directory)));
        }
        await Rejected<ArgumentException>("ABC", new[] { "report.json" });
        await Rejected<ArgumentException>(Universe, Array.Empty<string>());
        await Rejected<ArgumentException>(Universe, new[] { "report.json", "report.json" });
        await Rejected<ArgumentException>(Universe, new[] { "../outside.json" });
        await Rejected<ArgumentException>(Universe, new[] { "sub/inner.json" });
        await Rejected<ArgumentException>(Universe, new[] { RlPortfolioContract.ArtifactManifest });
        await Rejected<ArgumentException>(Universe, new[] { "report.json.tmp" });
        await Rejected<FileNotFoundException>(Universe, new[] { "absent.json" });
        await Rejected<FileNotFoundException>(Universe, new[] { "sub" });
        Assert.Throws<ArgumentException>(() => store.Begin(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PublishAsync(id, Universe, Calendar, null!));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishAsync(id, Universe, Calendar, new[] { "report.json" }, cancelled.Token));
        Assert.False(File.Exists(Manifest(directory)));
        Assert.Throws<ArgumentException>(() => new RlPortfolioArtifactStore(" "));
    }

    [Fact]
    public void TheExistingRegistryTargetsAndContractsAreUntouchedByThePortfolioIdentifiers()
    {
        Assert.NotEqual(RlFiles.Target, RlPortfolioContract.SpecificationId);
        Assert.NotEqual(RlContinuousContract.Target, RlPortfolioContract.SpecificationId);
        Assert.Equal("RLPortfolio", RlPortfolioContract.ArtifactFolder);
        Assert.DoesNotContain(RlPortfolioContract.ArtifactFolder, RlContract.ToWireDefaults().Keys.Concat(RlContract.ToWireDefaults().Values.OfType<string>()));
    }
}
