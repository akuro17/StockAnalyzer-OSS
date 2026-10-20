using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The labels of the Data Window rows are shown in the language of the user. Every TextBlock of DataWindowView.axaml takes its text from a binding or from a
/// localization key, every key used by the view exists in en.json and ja.json, and the English text of the rows of the special charts is the text that
/// was shown before the labels were moved into the locale files.
/// </summary>
public class DataWindowViewLocalizationTests
{
    private static readonly Regex LiteralTextBlockText = new(@"<TextBlock\b[^>]*?\bText=""(?<text>[^""{][^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LiteralMenuItemHeader = new(@"<MenuItem\b[^>]*?\bHeader=""(?<text>[^""{][^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LocalizeKey = new(@"\{l:Localize\s+(?<key>[A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    private static string AvaloniaPath(params string[] parts) => Path.Combine(new[] { TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia" }.Concat(parts).ToArray());

    private static string ViewSource() => File.ReadAllText(AvaloniaPath("Views", "DataWindowView.axaml"));

    private static Dictionary<string, string> LoadLocale(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AvaloniaPath("Resources", "Locales", fileName)));
        // The locale files may hold nested objects next to the flat text keys; only text values are labels.
        return document.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    [Fact]
    public void EveryTextBlockTakesItsTextFromABindingOrALocalizationKey()
    {
        var literals = LiteralTextBlockText.Matches(ViewSource()).Select(m => m.Groups["text"].Value).ToList();

        Assert.True(literals.Count == 0, "TextBlock elements with a literal Text: " + string.Join(", ", literals.Select(t => $"\"{t}\"")));
    }

    [Fact]
    public void EveryMenuItemTakesItsHeaderFromABindingOrALocalizationKey()
    {
        var literals = LiteralMenuItemHeader.Matches(ViewSource()).Select(m => m.Groups["text"].Value).ToList();

        Assert.True(literals.Count == 0, "MenuItem elements with a literal Header: " + string.Join(", ", literals.Select(t => $"\"{t}\"")));
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("ja.json")]
    public void EveryLocalizationKeyOfTheViewExistsAndIsNotEmpty(string localeFile)
    {
        var locale = LoadLocale(localeFile);
        var keys = LocalizeKey.Matches(ViewSource()).Select(m => m.Groups["key"].Value).Distinct().ToList();

        Assert.NotEmpty(keys);
        var missing = keys.Where(k => !locale.TryGetValue(k, out var value) || string.IsNullOrWhiteSpace(value)).ToList();
        Assert.True(missing.Count == 0, $"{localeFile} lacks: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("DataWindow_Row_Move", "Move:")]
    [InlineData("DataWindow_Row_Count", "Count:")]
    [InlineData("DataWindow_Row_BoxCount", "Count:")]
    [InlineData("DataWindow_Row_Reversal", "Reversal:")]
    [InlineData("DataWindow_Row_Update", "Update:")]
    [InlineData("DataWindow_Row_Value", "Value:")]
    [InlineData("DataWindow_Row_VolumeAverage", "VolAvg:")]
    [InlineData("DataWindow_Row_ReversalDate", "Reversal Date:")]
    [InlineData("DataWindow_Row_UpdateDate", "Update Date:")]
    [InlineData("DataWindow_Row_TrendDays", "Days:")]
    public void SpecialChartRowLabels_KeepTheEnglishTextAndHaveAJapaneseText(string key, string englishBefore)
    {
        var en = LoadLocale("en.json");
        var ja = LoadLocale("ja.json");

        Assert.Equal(englishBefore, en[key]);
        Assert.False(string.IsNullOrWhiteSpace(ja[key]));
        Assert.NotEqual(en[key], ja[key]);
        Assert.Contains($"{{l:Localize {key}}}", ViewSource());
    }

    [Theory]
    [InlineData(16, "DataWindow_Row_ReversalDate", "TrendReversalDateText")]
    [InlineData(17, "DataWindow_Row_UpdateDate", "TrendUpdateDateText")]
    [InlineData(18, "DataWindow_Row_TrendDays", "TrendDaysText")]
    public void TrendDateRows_AreOnTheirGridRow_AndAreShownOnlyWhenTheirTextIsNotEmpty(int row, string key, string property)
    {
        var source = ViewSource();
        var label = new Regex($@"<TextBlock Grid\.Row=""{row}"" Grid\.Column=""0"" Text=""\{{l:Localize {key}\}}""[^>]*?IsVisible=""\{{Binding {property}, Converter=\{{x:Static StringConverters\.IsNotNullOrEmpty\}}\}}""", RegexOptions.Singleline);
        var value = new Regex($@"<TextBlock Grid\.Row=""{row}"" Grid\.Column=""1"" Text=""\{{Binding {property}\}}""[^>]*?IsVisible=""\{{Binding {property}, Converter=\{{x:Static StringConverters\.IsNotNullOrEmpty\}}\}}""", RegexOptions.Singleline);

        Assert.Matches(label, source);
        Assert.Matches(value, source);
    }

    [Fact]
    public void TheMarketDataGrid_HasARowForEveryRowIndexUsedByTheView()
    {
        var source = ViewSource();
        var grid = Regex.Match(source, @"<Grid ColumnDefinitions=""Auto,\*"" RowDefinitions=""(?<rows>[^""]+)"">(?<body>.*?)</Grid>", RegexOptions.Singleline);
        Assert.True(grid.Success);
        int definedRows = grid.Groups["rows"].Value.Split(',').Length;
        int highestUsedRow = Regex.Matches(grid.Groups["body"].Value, @"Grid\.Row=""(?<row>\d+)""").Select(m => int.Parse(m.Groups["row"].Value)).Max();

        Assert.True(highestUsedRow < definedRows, $"the grid has {definedRows} rows but the view uses row {highestUsedRow}");
    }
}
