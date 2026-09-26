using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class WorkspaceSettingsColumnTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa_ws_coltpl_" + Guid.NewGuid().ToString("N"));

    public WorkspaceSettingsColumnTemplateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task LegacyFileWithoutPerListSelections_LoadsWithEmptyMap_AndKeepsColumns()
    {
        var path = Path.Combine(_dir, "legacy.json");
        await File.WriteAllTextAsync(path, "{\"TickerListVisibleColumns\":[\"Symbol\",\"Name\"]}");
        using var service = new WorkspaceSerializationService();

        var settings = await service.LoadAsync(path);

        Assert.NotNull(settings);
        Assert.Empty(settings!.TickerListColumnTemplateByList);
        Assert.Equal(new[] { "Symbol", "Name" }, settings.TickerListVisibleColumns);
    }

    [Fact]
    public async Task PerListSelections_RoundTripThroughSaveAndLoad()
    {
        var path = Path.Combine(_dir, "roundtrip.json");
        var listId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        using var service = new WorkspaceSerializationService();

        await service.SaveAsync(new WorkspaceSettings { TickerListColumnTemplateByList = new Dictionary<Guid, Guid> { [listId] = templateId } }, path);
        var restored = await service.LoadAsync(path);

        Assert.Equal(templateId, restored!.TickerListColumnTemplateByList[listId]);
    }
}
