using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services.Watchlist;

/// <summary>The operator family a ticker field belongs to, as <see cref="FilterRuleEvaluator"/> evaluates it.</summary>
public enum FilterFieldKind
{
    /// <summary>The field holds a number (decimal, double, float, int, long): ordering and equality operators.</summary>
    Numeric,

    /// <summary>The field holds text, a flag or a date-time (compared through its text): contains and equality operators.</summary>
    Text,

    /// <summary>The Tag field: comma- or semicolon-separated tokens, contains and equality operators.</summary>
    Tag,
}

/// <summary>Which part of a <see cref="FilterRule"/> a <see cref="FilterRuleProblem"/> is about.</summary>
public enum FilterRuleProblemPart
{
    Field,
    Operator,
    Value,
}

/// <summary>Why a rule cannot be evaluated meaningfully.</summary>
public sealed record FilterRuleProblem(FilterRuleProblemPart Part, string Message);

/// <summary>
/// Strict check of a <see cref="FilterRule"/> before it is evaluated. <see cref="FilterRuleEvaluator"/> is lenient on purpose (a saved filter
/// that names an unknown field, or an operator the field cannot use, just matches nothing or falls back to "contains"); a rule that is typed in
/// by a caller must be told about such a mistake instead. The operator tables below are the spellings the evaluator really implements for each
/// family; <c>FilterRuleValidatorTests</c> pins every spelling to the evaluator's measured behaviour, so the two cannot drift apart unnoticed.
/// </summary>
public static class FilterRuleValidator
{
    // The evaluator takes the Tag branch for exactly this field name (ignoring case).
    private static readonly string TagFieldName = nameof(TickerRow.Tag);

    // The evaluator treats these CLR types as numbers (FilterRuleEvaluator.TryGetDecimalValue); every other type is compared as text.
    private static readonly HashSet<Type> NumericTypes = new() { typeof(decimal), typeof(double), typeof(float), typeof(int), typeof(long) };

    private static readonly IReadOnlyList<string> NumericOperators = Array.AsReadOnly(new[]
    {
        "==", "=", "Equals", "!=", "NotEquals",
        ">", "GreaterThan", ">=", "GreaterThanOrEqual", "<", "LessThan", "<=", "LessThanOrEqual",
    });

    private static readonly IReadOnlyList<string> TextOperators = Array.AsReadOnly(new[]
    {
        "*=", "Contains", "!*=", "NotContains", "!Contains",
        "==", "=", "Equals", "!=", "NotEquals",
    });

    private static readonly IReadOnlyList<string> TagOperators = Array.AsReadOnly(new[]
    {
        "*=", "Contains", "ContainsTag", "!*=", "NotContains", "!Contains",
        "==", "=", "Equals", "!=", "NotEquals",
    });

    /// <summary>The operator spellings (case-insensitive, surrounding spaces ignored) accepted for a field family.</summary>
    public static IReadOnlyList<string> OperatorsFor(FilterFieldKind kind) => kind switch
    {
        FilterFieldKind.Numeric => NumericOperators,
        FilterFieldKind.Text => TextOperators,
        FilterFieldKind.Tag => TagOperators,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The family of a known field or alias; false for an unknown name.</summary>
    public static bool TryGetFieldKind(string? field, out FilterFieldKind kind)
    {
        kind = FilterFieldKind.Text;
        if (!TickerRow.TryGetFieldType(field, out Type type))
        {
            return false;
        }

        kind = string.Equals(field, TagFieldName, StringComparison.OrdinalIgnoreCase)
            ? FilterFieldKind.Tag
            : NumericTypes.Contains(type) ? FilterFieldKind.Numeric : FilterFieldKind.Text;
        return true;
    }

    /// <summary>Null when the rule is meaningful; otherwise the first problem found, checked in the order field, operator, value.</summary>
    public static FilterRuleProblem? Validate(FilterRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!TryGetFieldKind(rule.Field, out FilterFieldKind kind))
        {
            return new FilterRuleProblem(FilterRuleProblemPart.Field, $"'{rule.Field}' is not a known ticker field.");
        }

        string op = rule.Operator?.Trim() ?? string.Empty;
        if (!OperatorsFor(kind).Contains(op, StringComparer.OrdinalIgnoreCase))
        {
            return new FilterRuleProblem(
                FilterRuleProblemPart.Operator,
                $"Operator '{rule.Operator}' is not valid for the {kind.ToString().ToLowerInvariant()} field '{rule.Field}'. Valid: {string.Join(", ", OperatorsFor(kind))}.");
        }

        if (rule.IsCompareToField)
        {
            if (!TickerRow.TryGetFieldType(rule.Value, out Type targetType))
            {
                return new FilterRuleProblem(FilterRuleProblemPart.Value, $"'{rule.Value}' is not a known ticker field to compare with.");
            }

            bool targetIsNumeric = NumericTypes.Contains(targetType);
            if (kind != FilterFieldKind.Tag && (kind == FilterFieldKind.Numeric) != targetIsNumeric)
            {
                return new FilterRuleProblem(
                    FilterRuleProblemPart.Value,
                    $"'{rule.Field}' is a {kind.ToString().ToLowerInvariant()} field and '{rule.Value}' is not; compare fields of the same kind.");
            }

            return null;
        }

        if (kind == FilterFieldKind.Numeric
            && !decimal.TryParse(rule.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            return new FilterRuleProblem(FilterRuleProblemPart.Value, $"'{rule.Value}' is not a number, but '{rule.Field}' is a numeric field.");
        }

        return null;
    }
}
