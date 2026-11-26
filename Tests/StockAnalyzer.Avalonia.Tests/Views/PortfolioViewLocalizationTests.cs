using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The portfolio screen and its transaction dialog take every label from a localization key that exists in en.json and ja.json, and the dialog takes the
/// lower bounds of its numeric boxes from <c>PortfolioInputLimits</c> (through the view model) instead of literals.
/// </summary>
public class PortfolioViewLocalizationTests
{
    private static readonly Regex LiteralHeader = new(@"<(?:TabItem|MenuItem|DataGridTextColumn|DataGridTemplateColumn)\b[^>]*?\sHeader=""(?<text>[^""{][^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LiteralMinimum = new(@"<NumericUpDown\b[^>]*?\sMinimum=""(?<text>[^""{][^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LocalizeKey = new(@"\{l:Localize\s+(?<key>[A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex NumericUpDownTag = new(@"<NumericUpDown\b[^>]*?/>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string AvaloniaPath(params string[] parts) => Path.Combine(new[] { TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia" }.Concat(parts).ToArray());

    private static string Source(string name) => File.ReadAllText(name == "SummaryView" ? AvaloniaPath("Views", "PortfolioSummaryView.axaml") : AvaloniaPath("Views", "Dialogs", "EditTransactionDialog.axaml"));

    private static Dictionary<string, string> LoadLocale(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AvaloniaPath("Resources", "Locales", fileName)));
        return document.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    [Fact]
    public void TheSummaryView_HasNoLiteralTabColumnOrMenuHeader()
    {
        var literals = LiteralHeader.Matches(Source("SummaryView")).Select(m => m.Groups["text"].Value).ToList();

        Assert.True(literals.Count == 0, "Headers with a literal text: " + string.Join(", ", literals.Select(t => $"\"{t}\"")));
    }

    [Fact]
    public void TheTransactionDialog_HasNoLiteralMinimumOnANumericBox()
    {
        var literals = LiteralMinimum.Matches(Source("Dialog")).Select(m => m.Groups["text"].Value).ToList();

        Assert.True(literals.Count == 0, "NumericUpDown boxes with a literal Minimum: " + string.Join(", ", literals));
    }

    [Fact]
    public void EveryNumericBoxOfTheTransactionDialog_ClipsAnOutOfRangeEntryInsteadOfDroppingIt()
    {
        var boxes = NumericUpDownTag.Matches(Source("Dialog")).Select(m => m.Value).ToList();

        Assert.NotEmpty(boxes);
        Assert.All(boxes, box => Assert.Contains("ClipValueToMinMax=\"True\"", box));
    }

    [Theory]
    [InlineData("en.json", "SummaryView")]
    [InlineData("ja.json", "SummaryView")]
    [InlineData("en.json", "Dialog")]
    [InlineData("ja.json", "Dialog")]
    public void EveryLocalizationKeyUsedIsDefinedAndNotEmpty(string localeFile, string view)
    {
        var locale = LoadLocale(localeFile);
        var keys = LocalizeKey.Matches(Source(view)).Select(m => m.Groups["key"].Value).Distinct().ToList();

        Assert.NotEmpty(keys);
        var missing = keys.Where(k => !locale.TryGetValue(k, out var value) || string.IsNullOrWhiteSpace(value)).ToList();
        Assert.True(missing.Count == 0, $"{localeFile} lacks (for {view}): " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("Portfolio_TabSummary", "Summary")]
    [InlineData("Portfolio_TabPositions", "Positions")]
    [InlineData("Portfolio_TabTransactions", "Transactions")]
    [InlineData("Portfolio_Col_Type", "Type")]
    [InlineData("Portfolio_Col_UnitPrice", "Price")]
    [InlineData("Portfolio_Col_Amount", "Amount")]
    [InlineData("Portfolio_Col_Target", "Target")]
    [InlineData("Portfolio_Menu_AddTransaction", "Add Transactions")]
    [InlineData("Portfolio_Menu_EditTransaction", "Edit Transaction")]
    [InlineData("Portfolio_Menu_DeleteTransaction", "Delete Transaction")]
    public void TheEnglishTextIsTheTextThatWasShownBeforeTheLabelsMovedIntoTheLocaleFiles(string key, string englishBefore)
    {
        Assert.Equal(englishBefore, LoadLocale("en.json")[key]);
        Assert.NotEqual(englishBefore, LoadLocale("ja.json")[key]);
    }

    [Theory]
    [InlineData("Col_Symbol", "Symbol")]
    [InlineData("Portfolio_Col_Fees", "Fees")]
    [InlineData("Portfolio_Col_Qty", "Qty")]
    public void TheTransactionsTabSharesTheSymbolFeesAndQuantityKeysWithTheOtherPortfolioTabs(string key, string english)
    {
        var view = Source("SummaryView");

        Assert.Contains("{l:Localize " + key + "}", view);
        Assert.Equal(english, LoadLocale("en.json")[key]);
    }

    [Theory]
    [InlineData("Portfolio_Col_Ticker")]
    [InlineData("Portfolio_Col_Fee")]
    [InlineData("Portfolio_Col_Quantity")]
    public void TheDuplicateKeysThatOnlyRepeatASharedMeaningAreNotDefinedAnymore(string key)
    {
        Assert.DoesNotContain(key, LoadLocale("en.json").Keys);
        Assert.DoesNotContain(key, LoadLocale("ja.json").Keys);
        Assert.DoesNotContain("{l:Localize " + key + "}", Source("SummaryView"));
    }
}
