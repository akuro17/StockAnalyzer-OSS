using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed partial class RlGenerationAndQValueTests
{
    private const string PlaybackSymbol = "PBK";
    private const int PlaybackBars = 1_400;

    /// <summary>A symbol stored under the usual names, then both folders moved to names the application never uses.</summary>
    private RlLocalSourceFolders OddlyNamedPlaybackFolders()
    {
        string tree = Path.Combine(_root, "playback_tree");
        RlSourceFixture.WriteLocalSymbol(tree, PlaybackSymbol, RlSourceFixture.FirstDay, PlaybackBars);
        string prices = Path.Combine(_root, "playback_elsewhere", "Prices"), meta = Path.Combine(_root, "playback_elsewhere", "Meta");
        Directory.CreateDirectory(Path.GetDirectoryName(prices)!);
        Directory.Move(Path.Combine(tree, "Daily"), prices);
        Directory.Move(Path.Combine(tree, "Metadata"), meta);
        return new RlLocalSourceFolders(prices, meta);
    }

    /// <summary>The replay window of the registry-backed playback tools reads the configured folders; with none given it reads only the fixed Data folders.</summary>
    [Fact]
    public async Task PlaybackAnchorRange_ReadsTheNamedFoldersWhileTheDefaultReadsOnlyTheFixedDataFolders()
    {
        var folders = OddlyNamedPlaybackFolders();
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(Candidate().Directory);
        var replay = new QValueService(registry);

        var byDefault = new RlDialogDataService(registry, replay, _root);
        await Assert.ThrowsAsync<RlExternalReplayException>(() => byDefault.GetAnchorRangeAsync(generation.ModelId, PlaybackSymbol, CancellationToken.None));
        await Assert.ThrowsAsync<RlExternalReplayException>(() => byDefault.GetEphemeralAnchorRangeAsync(PlaybackSymbol, CancellationToken.None));

        var named = new RlDialogDataService(registry, replay, _root, folders);
        var range = await named.GetAnchorRangeAsync(generation.ModelId, PlaybackSymbol, CancellationToken.None);
        var ephemeral = await named.GetEphemeralAnchorRangeAsync(PlaybackSymbol, CancellationToken.None);

        Assert.True(range.EarliestAnchor <= range.LatestAnchor);
        Assert.Equal(range, ephemeral);
    }
}
