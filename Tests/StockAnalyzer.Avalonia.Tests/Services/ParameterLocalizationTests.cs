using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class ParameterLocalizationTests
{
    [AvaloniaFact]
    public void Build_JapaneseLocale_ResolvesCategoryAndPropertyNames()
    {
        try
        {
            LocalizationManager.Instance.Initialize("ja");

            var builder = new ParameterViewBuilder();
            var param = new CoreIchimokuParameter();

            var control = builder.Build(param);
            var stackPanel = Assert.IsType<StackPanel>(control);

            // Verify category headers in Japanese
            var headerTexts = stackPanel.Children
                .OfType<Border>()
                .Where(b => b.Child is TextBlock)
                .Select(b => ((TextBlock)b.Child!).Text)
                .ToList();

            Assert.Contains("期間設定", headerTexts);
            Assert.Contains("シフト設定", headerTexts);

            // Verify property label texts in Japanese
            var labelTexts = stackPanel.Children
                .OfType<Border>()
                .Where(b => b.Child is Grid)
                .Select(b => (Grid)b.Child!)
                .Select(g => g.Children.OfType<Border>().FirstOrDefault())
                .Where(b => b?.Child is TextBlock)
                .Select(b => ((TextBlock)b!.Child!).Text)
                .ToList();

            Assert.Contains("転換線 (期間)", labelTexts);
            Assert.Contains("基準線 (期間)", labelTexts);
            Assert.Contains("先行スパンB (期間)", labelTexts);
            Assert.Contains("オフセット / シフト", labelTexts);
        }
        finally
        {
            LocalizationManager.Instance.Initialize("en");
        }
    }

    [AvaloniaFact]
    public void Build_EnglishLocale_ResolvesCategoryAndPropertyNames()
    {
        try
        {
            LocalizationManager.Instance.Initialize("en");

            var builder = new ParameterViewBuilder();
            var param = new CoreIchimokuParameter();

            var control = builder.Build(param);
            var stackPanel = Assert.IsType<StackPanel>(control);

            // Verify category headers in English
            var headerTexts = stackPanel.Children
                .OfType<Border>()
                .Where(b => b.Child is TextBlock)
                .Select(b => ((TextBlock)b.Child!).Text)
                .ToList();

            Assert.Contains("Periods", headerTexts);
            Assert.Contains("Shifting", headerTexts);

            // Verify property label texts in English
            var labelTexts = stackPanel.Children
                .OfType<Border>()
                .Where(b => b.Child is Grid)
                .Select(b => (Grid)b.Child!)
                .Select(g => g.Children.OfType<Border>().FirstOrDefault())
                .Where(b => b?.Child is TextBlock)
                .Select(b => ((TextBlock)b!.Child!).Text)
                .ToList();

            Assert.Contains("Tenkan-sen (Period)", labelTexts);
            Assert.Contains("Kijun-sen (Period)", labelTexts);
            Assert.Contains("Senkou Span B (Period)", labelTexts);
            Assert.Contains("Offset", labelTexts);
        }
        finally
        {
            LocalizationManager.Instance.Initialize("en");
        }
    }
}
