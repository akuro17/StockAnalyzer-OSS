using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// The settings panel of the four block chart overlays is built by reflection (ParameterViewBuilder): its labels come from <c>Param_{PropertyName}</c>, its group titles from <c>Category_{Name}</c> and the items of an enum
/// drop-down from <c>Enum_{EnumType}_{Member}</c> (the drop-down opts in with <see cref="EnumDisplaySortAttribute"/>). A key that is missing in a locale shows as English text or the raw member name, and
/// <see cref="LocalizationKeyCoverageTests"/> does not see it (these keys are looked up from code, not from XAML), so this test names the missing key and the locale.
/// </summary>
public class BlockChartOverlayLocalizationTests
{
    private static readonly Type[] ParameterTypes =
    {
        typeof(CoreRenkoChartParameter), typeof(CorePointAndFigureChartParameter), typeof(CoreKagiChartParameter), typeof(CoreThreeLineBreakChartParameter),
    };

    private static readonly string[] Locales = { "en", "ja" };

    private static HashSet<string> LoadKeys(string languageCode)
    {
        var assembly = typeof(LocalizationManager).Assembly;
        using var stream = assembly.GetManifestResourceStream(string.Format(LocalizationManager.LocaleResourceNameFormat, languageCode));
        Assert.True(stream != null, $"Embedded locale resource not found for '{languageCode}'.");

        using var reader = new StreamReader(stream!, Encoding.UTF8);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<PropertyInfo> PanelProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<DisplayNameAttribute>() != null);

    [Fact]
    public void EveryPanelLabelCategoryAndEnumItem_HasAKeyInEveryLocale()
    {
        var problems = new List<string>();

        foreach (string locale in Locales)
        {
            var keys = LoadKeys(locale);

            foreach (var type in ParameterTypes)
            {
                foreach (var property in PanelProperties(type))
                {
                    if (!keys.Contains($"Param_{property.Name}")) problems.Add($"{locale}.json: Param_{property.Name} ({type.Name})");

                    var category = property.GetCustomAttribute<CategoryAttribute>()?.Category;
                    if (!string.IsNullOrEmpty(category) && !keys.Contains($"Category_{category}")) problems.Add($"{locale}.json: Category_{category} ({type.Name}.{property.Name})");

                    if (property.PropertyType.IsEnum)
                    {
                        Assert.NotNull(property.GetCustomAttribute<EnumDisplaySortAttribute>()); // without it the drop-down shows the raw member names
                        foreach (var member in Enum.GetNames(property.PropertyType))
                        {
                            if (!keys.Contains($"Enum_{property.PropertyType.Name}_{member}")) problems.Add($"{locale}.json: Enum_{property.PropertyType.Name}_{member} ({type.Name}.{property.Name})");
                        }
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, "Missing localization keys:\n  " + string.Join("\n  ", problems.Distinct().OrderBy(p => p, StringComparer.Ordinal)));
    }

    [Fact]
    public void TheSettingsPanelItems_ResolveToTheLocalizedTextOfTheMemberNotTheRawName()
    {
        // The converter the drop-down uses: a localized text exists, so it never falls back to the member name.
        Assert.NotEqual(nameof(ChartSizingMode.Fixed), StockAnalyzer.Avalonia.Converters.EnumToLocalizedDisplayNameConverter.ResolveDisplayText(ChartSizingMode.Fixed));
        Assert.NotEqual(nameof(ChartRoundingMode.NiceNumbers), StockAnalyzer.Avalonia.Converters.EnumToLocalizedDisplayNameConverter.ResolveDisplayText(ChartRoundingMode.NiceNumbers));
        Assert.NotEqual(nameof(AutoFallbackMode.Fixed), StockAnalyzer.Avalonia.Converters.EnumToLocalizedDisplayNameConverter.ResolveDisplayText(AutoFallbackMode.Fixed));
    }
}
