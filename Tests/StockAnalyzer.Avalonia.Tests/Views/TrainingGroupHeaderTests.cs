using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// S3a: the group headers of the three training windows come from one shared style file (<c>Assets/TrainingGroupHeader.axaml</c>). The values below are
/// the ones the Training Wizard had as local styles before the move (subtitle font size, bold, primary text brush; the headers after the first one have a
/// 12 px gap above), so the move must not change how the wizard renders.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingGroupHeaderTests
{
    /// <summary>The gap above "Advanced Settings" and "Results and management" before the style moved to the shared file.</summary>
    private const double PreChangeAdvancedTopMargin = 12;

    /// <summary>Asserts the resolved values of every group header of <paramref name="window"/>: the first has no extra margin, the others carry the gap above.</summary>
    public static void AssertSharedHeaderValues(Window window, params string[] expectedTexts)
    {
        // The headless test application does not define the font and brush resources the windows reference with DynamicResource; add what is missing for
        // the assertion (and take it out again), so the check proves that the style binds exactly these two keys.
        var resources = Application.Current!.Resources;
        var added = new System.Collections.Generic.List<string>();
        void Ensure(string key, object value)
        {
            if (Application.Current!.TryGetResource(key, window.ActualThemeVariant, out _)) return;
            resources[key] = value;
            added.Add(key);
        }
        Ensure("SubtitleFontSize", 21d);
        Ensure("Brush.Text.Primary", new SolidColorBrush(Colors.DarkOrange));
        try
        {
            FooterOrderStandardTests.Pump();
            var headers = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("groupHeader")).ToArray();
            Assert.Equal(expectedTexts, headers.Select(h => h.Text));
            Assert.True(window.TryFindResource("SubtitleFontSize", window.ActualThemeVariant, out var size));
            Assert.True(window.TryFindResource("Brush.Text.Primary", window.ActualThemeVariant, out var brush));
            for (int i = 0; i < headers.Length; i++)
            {
                Assert.Equal((double)size!, headers[i].FontSize);
                Assert.Equal(FontWeight.Bold, headers[i].FontWeight);
                Assert.Equal(((ISolidColorBrush)brush!).Color, ((ISolidColorBrush)headers[i].Foreground!).Color);
                Assert.Equal(i == 0 ? new Thickness(0) : new Thickness(0, PreChangeAdvancedTopMargin, 0, 0), headers[i].Margin);
                Assert.Equal(i > 0, headers[i].Classes.Contains("advancedGroupHeader"));
            }
        }
        finally { foreach (var key in added) resources.Remove(key); }
    }

    [AvaloniaFact]
    public void TrainingWizard_HeadersKeepTheirRenderedValuesAndHaveNoExplanationBrackets()
    {
        LocalizationManager.Instance.Initialize("en");
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            FooterOrderStandardTests.Pump();
            AssertSharedHeaderValues(window, "Basic Settings", "Advanced Settings");
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public void SharedMarginTokenIsTheValueTheWizardHadBefore()
    {
        Assert.Equal(PreChangeAdvancedTopMargin, TrainingGroupHeaderLayout.AdvancedTopMargin);
        Assert.Equal(new Thickness(0, PreChangeAdvancedTopMargin, 0, 0), TrainingGroupHeaderLayout.AdvancedMargin);
    }
}
