using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// Guard against silent localization gaps: <see cref="LocalizationManager.Get"/> returns the debug
/// fallback <c>"[" + key + "]"</c> for an unknown key, so a <c>{l:Localize KEY}</c> added to XAML but
/// not to every locale file only surfaces when a user reports the raw <c>[KEY]</c> text in the UI
/// (as happened with <c>Group_Parameters</c> / <c>Group_Colors</c> and
/// <c>FilterSettings_ToolTip_TextInputMode</c>). This test fails instead, naming the key and the file.
/// Scope: keys referenced from <c>*.axaml</c> via the <c>l:Localize</c> markup extension, plus the
/// keys <c>DataWindowViewModel</c> resolves from code (see
/// <see cref="DataWindowViewModel_CodeReferencedLocalizationKeys_ResolveInEveryLocale"/>). Keys looked
/// up from code elsewhere (<c>LocalizationManager.Instance["..."]</c>) are still not covered here.
/// </summary>
public class LocalizationKeyCoverageTests
{
    // Split "StockAnalyzer.Avalonia.Resources.Locales.{0}.json" once, so the resource-name shape lives
    // only in LocalizationManager and this test stays correct if it ever changes.
    private static readonly string LocaleResourcePrefix =
        LocalizationManager.LocaleResourceNameFormat.Substring(0, LocalizationManager.LocaleResourceNameFormat.IndexOf("{0}", StringComparison.Ordinal));
    private static readonly string LocaleResourceSuffix =
        LocalizationManager.LocaleResourceNameFormat.Substring(LocalizationManager.LocaleResourceNameFormat.IndexOf("{0}", StringComparison.Ordinal) + "{0}".Length);

    /// <summary>Language codes of every embedded <c>Resources/Locales/*.json</c> actually shipped in
    /// the Avalonia assembly - discovered, not hardcoded, so a newly added locale is covered
    /// automatically.</summary>
    private static readonly string[] Locales = DiscoverLocaleCodes();

    private static readonly Regex LocalizeKeyPattern =
        new(@"\{\s*l:Localize\s+(?:Key\s*=\s*)?([A-Za-z0-9_]+)", RegexOptions.Compiled);

    // A string-literal argument to GetString("..."); the parameter declaration GetString(string key)
    // and the variable form Instance[key] both fail the quotes, so only real key literals match.
    private static readonly Regex GetStringKeyPattern =
        new(@"GetString\(\s*""([A-Za-z0-9_]+)""\s*\)", RegexOptions.Compiled);

    private static string[] DiscoverLocaleCodes()
    {
        return typeof(LocalizationManager).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(LocaleResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(LocaleResourceSuffix, StringComparison.Ordinal)
                        && n.Length > LocaleResourcePrefix.Length + LocaleResourceSuffix.Length)
            .Select(n => n.Substring(LocaleResourcePrefix.Length, n.Length - LocaleResourcePrefix.Length - LocaleResourceSuffix.Length))
            .Where(code => !code.Contains('.'))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
    }

    private static string LocaleResourceName(string languageCode) =>
        string.Format(LocalizationManager.LocaleResourceNameFormat, languageCode);

    [Fact]
    public void EveryLocalizeKeyInXaml_ResolvesInEveryLocale()
    {
        var localeKeys = Locales.ToDictionary(l => l, LoadLocaleKeys);
        var axamlKeys = ScanAxamlLocalizeKeys();

        Assert.NotEmpty(axamlKeys);

        var problems = new List<string>();
        foreach (var (key, files) in axamlKeys.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var locale in Locales)
            {
                if (!localeKeys[locale].Contains(key))
                {
                    problems.Add($"  '{key}' missing from {locale}.json  (used in: {string.Join(", ", files.OrderBy(f => f, StringComparer.Ordinal))})");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "XAML l:Localize keys with no localization entry:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Scan_FindsAPlausibleNumberOfKeys()
    {
        var axamlKeys = ScanAxamlLocalizeKeys();
        Assert.True(axamlKeys.Count > 100, $"Expected the axaml scan to find >100 distinct l:Localize keys, found {axamlKeys.Count}. The scan or the source path is probably broken.");

        Assert.True(Locales.Length >= 2, $"Locale discovery found {Locales.Length} embedded locale(s) ({string.Join(", ", Locales)}); expected at least 'en' and 'ja'. The manifest-resource scan is probably broken.");
        Assert.Contains("en", Locales);

        foreach (var locale in Locales)
        {
            Assert.True(LoadLocaleKeys(locale).Count > 100, $"Locale '{locale}' resolved to too few keys; embedded resource lookup is probably broken.");
        }
    }

    /// <summary>
    /// A locale JSON with a repeated top-level key is silently resolved last-wins by
    /// <see cref="LocalizationManager"/>, so appending a block without de-duplicating against the
    /// existing keys changes resolved strings invisibly (this is exactly what broke the Ichimoku
    /// parameter labels). Fail loudly instead, naming every duplicated key.
    /// </summary>
    [Fact]
    public void NoLocaleFileHasDuplicateTopLevelKeys()
    {
        var problems = new List<string>();
        foreach (var locale in Locales)
        {
            var dups = LoadLocaleKeyList(locale)
                .GroupBy(k => k, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"  {locale}.json: '{g.Key}' x{g.Count()}")
                .OrderBy(s => s, StringComparer.Ordinal);
            problems.AddRange(dups);
        }

        Assert.True(problems.Count == 0, "Duplicate top-level keys in locale files:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// Every localization key must exist in every shipped locale; a key present in one locale only
    /// surfaces as the raw <c>[KEY]</c> fallback for users of the other language.
    /// </summary>
    [Fact]
    public void LocaleKeySets_AreIdenticalAcrossLocales()
    {
        var sets = Locales.ToDictionary(l => l, LoadLocaleKeys);

        var problems = new List<string>();
        foreach (var locale in Locales)
        {
            foreach (var other in Locales.Where(o => o != locale))
            {
                foreach (var key in sets[locale].Except(sets[other]).OrderBy(k => k, StringComparer.Ordinal))
                    problems.Add($"  '{key}' is in {locale}.json but missing from {other}.json");
            }
        }

        Assert.True(problems.Count == 0, "Locale key sets diverge:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <see cref="StockAnalyzer.Avalonia.ViewModels.DataWindowViewModel"/> resolves its section-title
    /// and unit-label strings by calling <c>ILocalizationService.GetString("...")</c> from code, not via
    /// <c>l:Localize</c> in XAML, so <see cref="EveryLocalizeKeyInXaml_ResolvesInEveryLocale"/> never
    /// sees them and <see cref="LocaleKeySets_AreIdenticalAcrossLocales"/> stays green while a key is
    /// absent from <em>every</em> locale. That is exactly how <c>DataWindow_Section_Drawings</c> shipped
    /// missing and surfaced as the raw <c>[DataWindow_Section_Drawings]</c> fallback in the Data tab.
    /// This scans the view model source for the literal keys instead of enumerating them here, so keys
    /// added to <c>DataWindowViewModel</c> later are covered without editing this test.
    /// </summary>
    [Fact]
    public void DataWindowViewModel_CodeReferencedLocalizationKeys_ResolveInEveryLocale()
    {
        var source = File.ReadAllText(
            Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", "ViewModels", "DataWindowViewModel.cs"));

        var keys = GetStringKeyPattern.Matches(source)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // Sentinel: if the regex or the source path breaks, fail loudly rather than pass vacuously.
        Assert.True(keys.Count >= 3, $"Expected to scan >=3 GetString(\"...\") keys from DataWindowViewModel.cs, found {keys.Count}.");

        var localeKeys = Locales.ToDictionary(l => l, LoadLocaleKeys);

        var problems = new List<string>();
        foreach (var key in keys)
        {
            foreach (var locale in Locales)
            {
                if (!localeKeys[locale].Contains(key))
                    problems.Add($"  '{key}' (DataWindowViewModel.cs) missing from {locale}.json");
            }
        }

        Assert.True(problems.Count == 0, "DataWindow code-referenced localization keys with no entry:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// Depth-1 property names in file order, duplicates preserved (unlike <see cref="LoadLocaleKeys"/>
    /// whose HashSet collapses them). Uses <see cref="Utf8JsonReader"/> so it is insensitive to
    /// formatting.
    /// </summary>
    private static IReadOnlyList<string> LoadLocaleKeyList(string languageCode)
    {
        var assembly = typeof(LocalizationManager).Assembly;
        var resourceName = LocaleResourceName(languageCode);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.True(stream != null, $"Embedded locale resource not found: {resourceName}");

        using var ms = new MemoryStream();
        stream!.CopyTo(ms);

        var reader = new Utf8JsonReader(ms.ToArray());
        var keys = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                var name = reader.GetString()!;
                if (!name.StartsWith("_", StringComparison.Ordinal))
                    keys.Add(name);
            }
        }
        return keys;
    }

    private static Dictionary<string, IReadOnlyList<string>> ScanAxamlLocalizeKeys()
    {
        var avaloniaDir = Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia");
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(avaloniaDir, "*.axaml", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/bin/") || normalized.Contains("/obj/"))
                continue;

            var rel = Path.GetRelativePath(avaloniaDir, file).Replace('\\', '/');
            var text = File.ReadAllText(file);

            foreach (Match m in LocalizeKeyPattern.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!result.TryGetValue(key, out var files))
                {
                    files = new List<string>();
                    result[key] = files;
                }
                if (!files.Contains(rel))
                    files.Add(rel);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    private static HashSet<string> LoadLocaleKeys(string languageCode)
    {
        var assembly = typeof(LocalizationManager).Assembly;
        var resourceName = LocaleResourceName(languageCode);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.True(stream != null, $"Embedded locale resource not found: {resourceName}");

        using var reader = new StreamReader(stream!, Encoding.UTF8);
        using var document = JsonDocument.Parse(reader.ReadToEnd());

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("_", StringComparison.Ordinal))
                continue;
            if (property.Value.ValueKind == JsonValueKind.String)
                keys.Add(property.Name);
        }
        return keys;
    }
}
