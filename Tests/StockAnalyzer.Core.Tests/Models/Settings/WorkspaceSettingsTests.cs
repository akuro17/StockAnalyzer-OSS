using System.Collections.Generic;
using System.Text.Json;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Settings;

public class WorkspaceSettingsTests
{
    [Fact]
    public void CustomTickerCategories_DefaultsToEmpty()
    {
        var settings = new WorkspaceSettings();

        Assert.Empty(settings.CustomTickerCategories);
    }

    [Fact]
    public void CustomTickerCategories_RoundTripsThroughJson()
    {
        // Scoped to the new list type itself (not the full WorkspaceSettings graph): WorkspaceSettings
        // also carries polymorphic indicator settings that need a dedicated JsonSerializerOptions
        // (configured by the production settings-save service) to serialize at all. Exercising that
        // configuration is out of scope for this feature; round-tripping the new type in isolation is
        // sufficient to prove it serializes/deserializes correctly.
        var folder = new TickerListFolderSettings
        {
            Name = "My Sector List",
            ChildTickers = new List<string> { "AAPL", "MSFT" },
        };
        var category = new TickerParentCategorySettings
        {
            Name = "My Sector",
            Folders = new List<TickerListFolderSettings> { folder },
        };
        var categories = new List<TickerParentCategorySettings> { category };

        var json = JsonSerializer.Serialize(categories);
        var deserialized = JsonSerializer.Deserialize<List<TickerParentCategorySettings>>(json);

        Assert.NotNull(deserialized);
        var restoredCategory = Assert.Single(deserialized!);
        Assert.Equal(category.Id, restoredCategory.Id);
        Assert.Equal(category.Name, restoredCategory.Name);
        var restoredFolder = Assert.Single(restoredCategory.Folders);
        Assert.Equal(folder.Id, restoredFolder.Id);
        Assert.Equal(folder.Name, restoredFolder.Name);
        Assert.Equal(folder.ChildTickers, restoredFolder.ChildTickers);
    }
}
