using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed partial class RlGenerationAndQValueTests
{
    /// <summary>The model archive end to end on two real registries: what one registry exports, the other registers under the same id and does not activate.</summary>
    [Fact]
    public async Task GenerationArchive_ExportedFromOneRegistryIsRegisteredByAnotherWithTheSameIdAndNotActivated()
    {
        var candidate = Candidate();
        using var source = new ModelGenerationRegistry(RegistryRoot);
        var registered = await source.RegisterRlAsync(candidate.Directory);
        await source.ActivateRlAsync(registered.ModelId);
        var service = new RlModelArchiveService();
        string zip = Path.Combine(_root, "generation_archive.zip");
        using (var lease = source.AcquireRl(registered.ModelId))
        {
            Assert.NotNull(lease);
            await service.ExportGenerationArchiveAsync(Path.GetDirectoryName(lease.ModelPath)!, zip);
        }

        var extracted = await service.ImportArchiveAsync(zip, Path.Combine(_root, "archive_staging"));
        Assert.True(extracted.Success, extracted.Error);
        Assert.Equal(RlModelArchiveService.GenerationKind, extracted.ArchiveKind);
        Assert.Equal(registered.ModelId, extracted.Id);

        using var target = new ModelGenerationRegistry(Path.Combine(_root, "registry_target"));
        var imported = await target.RegisterRlAsync(extracted.TargetDirectory);

        Assert.Equal(registered.ModelId, imported.ModelId);
        Assert.Equal(registered.Files, imported.Files);
        Assert.Single(target.ListRl());
        Assert.Null(target.RlActiveId);
        Assert.Equal(registered.ModelId, source.RlActiveId);
    }
}
