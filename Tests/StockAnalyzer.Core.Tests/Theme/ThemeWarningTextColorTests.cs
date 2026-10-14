using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Core.Tests.Theme;

/// <summary>
/// The warning text color is a theme item whose default depends on the theme mode, and a theme file written before the item
/// existed must still load with the default of its own mode. Uses an isolated theme file path (never the real Data/Config).
/// </summary>
public sealed class ThemeWarningTextColorTests : IDisposable
{
    private const string WarningProperty = nameof(ThemeColors.ShellWarningText);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"theme-warning-{Guid.NewGuid():N}");
    private readonly string _themeFilePath;

    public ThemeWarningTextColorTests()
    {
        Directory.CreateDirectory(_directory);
        _themeFilePath = Path.Combine(_directory, "user_theme.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void DarkPreset_SecondaryText_IsBac2ce()
    {
        Assert.Equal(IndicatorColor.FromUInt(0xFFBAC2CE), ThemeColors.Dark.ShellSecondaryText);
        Assert.Equal(IndicatorColor.FromUInt(0xFFBAC2CE), new ThemeColors().ShellSecondaryText);
    }

    [Fact]
    public void Presets_WarningText_UsesTheDefaultOfTheirMode()
    {
        Assert.Equal(ThemeColors.DefaultWarningTextDark, ThemeColors.Dark.ShellWarningText);
        Assert.Equal(ThemeColors.DefaultWarningTextLight, ThemeColors.Light.ShellWarningText);
        Assert.NotEqual(ThemeColors.Dark.ShellWarningText, ThemeColors.Light.ShellWarningText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAsync_FileWithoutWarningText_UsesTheDefaultOfItsOwnMode(bool dark)
    {
        var preset = dark ? ThemeColors.Dark : ThemeColors.Light;
        var colors = JsonNode.Parse(JsonSerializer.Serialize(preset))!.AsObject();
        Assert.True(colors.Remove(WarningProperty));
        var file = new JsonObject { ["Mode"] = dark ? (int)AppThemeMode.Dark : (int)AppThemeMode.Light, ["Colors"] = colors };
        await File.WriteAllTextAsync(_themeFilePath, file.ToJsonString());

        var manager = new ThemeManager(null, _themeFilePath);
        await manager.LoadAsync();

        var expected = dark ? ThemeColors.DefaultWarningTextDark : ThemeColors.DefaultWarningTextLight;
        Assert.Equal(expected, manager.CurrentTheme.ShellWarningText);
        Assert.Equal(expected, manager.GetCurrentColors()[ThemeColorKey.ShellWarningText]);
    }

    [Fact]
    public async Task StoredWarningText_RoundTripsThroughSaveAndLoad()
    {
        var custom = IndicatorColor.FromUInt(0xFF12AB34);
        var manager = new ThemeManager(null, _themeFilePath);
        manager.UpdateSingleColor(ThemeColorKey.ShellWarningText, custom);

        Assert.Equal(custom, manager.CurrentTheme.ShellWarningText);
        Assert.Equal(custom, manager.GetCurrentColors()[ThemeColorKey.ShellWarningText]);

        await manager.SaveAsync();
        Assert.Contains("\"ShellWarningText\": \"#FF12AB34\"", await File.ReadAllTextAsync(_themeFilePath));

        var reloaded = new ThemeManager(null, _themeFilePath);
        await reloaded.LoadAsync();
        Assert.Equal(custom, reloaded.CurrentTheme.ShellWarningText);
    }

    [Fact]
    public void WarningText_SurvivesWithCopies_AndSerializesAsArgbHex()
    {
        var custom = IndicatorColor.FromUInt(0xFF010203);
        var copy = ThemeColors.Light with { ShellWarningText = custom } with { ShellText = IndicatorColor.FromUInt(0xFF111111) };

        Assert.Equal(custom, copy.ShellWarningText);
        Assert.Contains("\"ShellWarningText\":\"#FF010203\"", JsonSerializer.Serialize(copy));
        Assert.Contains("\"ShellWarningText\":\"#FF9A6700\"", JsonSerializer.Serialize(ThemeColors.Light));
        Assert.Contains("\"ShellWarningText\":\"#FFFFC107\"", JsonSerializer.Serialize(ThemeColors.Dark));
    }
}
