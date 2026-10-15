using System;
using System.IO;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The warning color exists twice: as the Core default of the "Warning Text Color" theme item and as <c>Color.Semantic.Warning</c> of the
/// theme dictionaries in Colors.axaml (used by the warning icons and by every warning text until the theme item overrides it). Both
/// must stay equal, otherwise the Settings picker would show a color that differs from the one the screens use by default.
/// </summary>
public class WarningColorParityTests
{
    private const string ColorsFile = "Colors.axaml";
    private const string WarningKey = "Color.Semantic.Warning";

    private static string WarningColorOfDictionary(string themeKey)
    {
        string source = File.ReadAllText(Path.Combine(TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "Assets", ColorsFile));
        var match = Regex.Match(source,
            $@"<ResourceDictionary x:Key=""{themeKey}"">.*?<Color x:Key=""{Regex.Escape(WarningKey)}"">(?<color>#[0-9A-Fa-f]{{8}})</Color>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"{WarningKey} not found in the {themeKey} dictionary of {ColorsFile}.");
        return match.Groups["color"].Value.ToUpperInvariant();
    }

    [Fact]
    public void DarkWarningDefault_EqualsTheDarkAndDefaultDictionariesOfColorsAxaml()
    {
        string core = ThemeColors.DefaultWarningTextDark.ToString();

        Assert.Equal(core, WarningColorOfDictionary("Dark"));
        Assert.Equal(core, WarningColorOfDictionary("Default"));
    }

    [Fact]
    public void LightWarningDefault_EqualsTheLightDictionaryOfColorsAxaml()
    {
        Assert.Equal(ThemeColors.DefaultWarningTextLight.ToString(), WarningColorOfDictionary("Light"));
    }
}
