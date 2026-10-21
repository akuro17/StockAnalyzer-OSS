#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// The texts of the scatter metric registration window. <c>LocalizationKeyCoverageTests</c> checks the keys the markup uses; the keys the view model
/// looks up in code are not covered there, so they are checked here, together with the whole <c>ScatterMetric_*</c> family.
/// </summary>
public class ScatterMetricLocalizationTests
{
    private static readonly string Root = StockAnalyzer.TestSupport.TestRepositoryRoot.Find();

    private static Dictionary<string, string> Load(string locale)
    {
        string path = Path.Combine(Root, "StockAnalyzer.Avalonia", "Resources", "Locales", locale + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    [Fact]
    public void EveryKeyTheViewModelLooksUpInCode_ExistsInEnglishAndJapanese()
    {
        string source = File.ReadAllText(Path.Combine(Root, "StockAnalyzer.Avalonia", "ViewModels", "Dialogs", "ScatterMetricRegistrationViewModel.cs"));
        string[] keys = Regex.Matches(source, @"(?:GetString|Format)\(\s*""([A-Za-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(source, @"AddSection\(\s*""([A-Za-z0-9_]+)""").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var en = Load("en");
        var ja = Load("ja");

        Assert.Contains("Settings_DiscardConfirm_Title", keys);
        Assert.Contains("ScatterMetric_Msg_Duplicate", keys);
        // The time frame combo names Day / Week / Month with the shared enum keys (the single-purpose Timeframe_Day is no longer looked up here).
        Assert.Contains("Enum_TimeFrame_D1", keys);
        Assert.Contains("Enum_TimeFrame_W1", keys);
        Assert.Contains("Enum_TimeFrame_MN1", keys);
        Assert.Contains("ScatterMetric_Tip_TimeFrameFixedDay", keys);
        Assert.DoesNotContain("Timeframe_Day", keys);
        Assert.Empty(keys.Where(k => !en.ContainsKey(k)));
        Assert.Empty(keys.Where(k => !ja.ContainsKey(k)));
    }

    [Fact]
    public void TheScatterMetricFamily_HasTheSameKeysInBothLanguages_WithTheSamePlaceholders()
    {
        var en = Load("en");
        var ja = Load("ja");
        string[] enKeys = en.Keys.Where(k => k.StartsWith("ScatterMetric_", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        string[] jaKeys = ja.Keys.Where(k => k.StartsWith("ScatterMetric_", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(enKeys.Length >= 14, "the plan names 14 keys for the registration window");
        Assert.Equal(enKeys, jaKeys);
        foreach (string key in enKeys)
        {
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(ja[key]), key);
            Assert.Equal(en[key].Contains("{0}"), ja[key].Contains("{0}"));
        }
    }

    [Fact]
    public void TheMessagesWithAPlaceholder_FormatWithOneArgument()
    {
        foreach (string locale in new[] { "en", "ja" })
        {
            var texts = Load(locale);
            foreach (string key in new[] { "ScatterMetric_Msg_Duplicate", "ScatterMetric_Msg_LimitReached", "ScatterMetric_Msg_Invalid", "ScatterMetric_Msg_LoadDropped" })
            {
                Assert.Contains("42", string.Format(texts[key], 42));
            }
        }
    }

    [Fact]
    public void TheDialogServiceDefault_IsAClosedWindow_AndTheViewModelIsRegisteredTransient()
    {
        IDialogService fake = new FakeDialogService();
        Assert.False(fake.ShowScatterMetricRegistrationDialogAsync().GetAwaiter().GetResult());

        var services = new ServiceCollection();
        services.AddCommonServices(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(ScatterMetricRegistrationViewModel));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }
}
