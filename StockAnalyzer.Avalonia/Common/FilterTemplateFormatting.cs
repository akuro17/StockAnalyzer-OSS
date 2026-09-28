using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Shared formatting for FilterTemplate/FilterSettings content, used by both the tree-level
/// FilterTemplatePickerDialog and the Filter Settings Templates section for hover/click preview.
/// </summary>
public static class FilterTemplateFormatting
{
    public static string FormatRule(FilterRule rule) => $"{rule.Field} {rule.Operator} {rule.Value}";

    public static List<string> ToRuleLines(FilterSettings? rootSettings)
    {
        if (rootSettings?.Rules == null) return new List<string>();
        return rootSettings.Rules
            .Select(FormatRule)
            .ToList();
    }

    public static string ToPreviewText(FilterSettings? rootSettings)
    {
        if (rootSettings == null) return string.Empty;

        var ruleLines = ToRuleLines(rootSettings);
        var text = ruleLines.Count > 0
            ? string.Join(Environment.NewLine, ruleLines)
            : LocalizationManager.Instance.Get("FilterTemplate_Preview_NoRules");

        if (rootSettings.Children is { Count: > 0 })
        {
            var childNames = string.Join(", ", rootSettings.Children.Select(c => c.Name));
            var subFiltersFormat = LocalizationManager.Instance.Get("FilterTemplate_Preview_SubFilters");
            text += Environment.NewLine + string.Format(subFiltersFormat, rootSettings.Children.Count, childNames);
        }

        return text;
    }
}
