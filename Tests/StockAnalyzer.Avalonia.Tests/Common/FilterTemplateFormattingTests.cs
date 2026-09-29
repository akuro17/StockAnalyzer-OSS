using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Common;

// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class FilterTemplateFormattingTests
{
    public FilterTemplateFormattingTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    [Fact]
    public void FormatRule_ReturnsFieldOperatorValue()
    {
        var rule = new FilterRule { Field = "PER", Operator = "LessThan", Value = "15" };

        Assert.Equal("PER LessThan 15", FilterTemplateFormatting.FormatRule(rule));
    }

    [Fact]
    public void ToRuleLines_FormatsEachRuleAsFieldOperatorValue()
    {
        var settings = new FilterSettings
        {
            Rules =
            {
                new FilterRule { Field = "PER", Operator = "LessThan", Value = "15" },
                new FilterRule { Field = "Tag", Operator = "Contains", Value = "Growth" }
            }
        };

        var lines = FilterTemplateFormatting.ToRuleLines(settings);

        Assert.Equal(new[] { "PER LessThan 15", "Tag Contains Growth" }, lines);
    }

    [Fact]
    public void ToPreviewText_NoRulesNoChildren_ReturnsNoRulesPlaceholder()
    {
        var settings = new FilterSettings();

        var preview = FilterTemplateFormatting.ToPreviewText(settings);

        Assert.Equal(LocalizationManager.Instance.Get("FilterTemplate_Preview_NoRules"), preview);
    }

    [Fact]
    public void ToPreviewText_WithChildren_AppendsSubFilterNames()
    {
        var settings = new FilterSettings
        {
            Rules = { new FilterRule { Field = "PER", Operator = "LessThan", Value = "15" } },
            Children =
            {
                new FilterSettings { Name = "SubA" },
                new FilterSettings { Name = "SubB" }
            }
        };

        var preview = FilterTemplateFormatting.ToPreviewText(settings);

        Assert.StartsWith("PER LessThan 15", preview);
        Assert.Contains("SubA", preview);
        Assert.Contains("SubB", preview);
    }

    [Fact]
    public void ToPreviewText_NullRootSettings_ReturnsEmpty()
    {
        var preview = FilterTemplateFormatting.ToPreviewText(null);

        Assert.Equal(string.Empty, preview);
    }
}
