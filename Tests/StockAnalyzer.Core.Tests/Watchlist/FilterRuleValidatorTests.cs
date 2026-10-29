using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Watchlist;
using Xunit;

namespace StockAnalyzer.Core.Tests.Watchlist;

public sealed class FilterRuleValidatorTests
{
    private static FilterRule Rule(string field, string op, string value, bool compareToField = false)
        => new() { Field = field, Operator = op, Value = value, IsCompareToField = compareToField };

    // ---- drift guard: every accepted spelling does what the validator's table claims ------------------------------------------------------
    // Vectors are the evaluator's answers to three probe values (T = match), measured on 2026-10-29 for plan 01 T0-iii.

    private static readonly TickerRow ProbeRow = new() { Symbol = "X", Close = 100m, Sector = "Technology", Tag = "AI, Cloud" };

    private static readonly Dictionary<string, string> NumericVectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["=="] = "FTF", ["="] = "FTF", ["Equals"] = "FTF",
        ["!="] = "TFT", ["NotEquals"] = "TFT",
        [">"] = "TFF", ["GreaterThan"] = "TFF",
        [">="] = "TTF", ["GreaterThanOrEqual"] = "TTF",
        ["<"] = "FFT", ["LessThan"] = "FFT",
        ["<="] = "FTT", ["LessThanOrEqual"] = "FTT",
    };

    private static readonly Dictionary<string, string> TextVectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["*="] = "TTF", ["Contains"] = "TTF",
        ["!*="] = "FFT", ["NotContains"] = "FFT", ["!Contains"] = "FFT",
        ["=="] = "FTF", ["="] = "FTF", ["Equals"] = "FTF",
        ["!="] = "TFT", ["NotEquals"] = "TFT",
    };

    private static readonly Dictionary<string, string> TagVectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["*="] = "TFF", ["Contains"] = "TFF", ["ContainsTag"] = "TFF",
        ["!*="] = "FTT", ["NotContains"] = "FTT", ["!Contains"] = "FTT",
        ["=="] = "FTF", ["="] = "FTF", ["Equals"] = "FTF",
        ["!="] = "TFT", ["NotEquals"] = "TFT",
    };

    private static string Vector(string field, string op, params string[] values)
        => string.Concat(values.Select(v => FilterRuleEvaluator.EvaluateRule(ProbeRow, Rule(field, op, v)) ? "T" : "F"));

    public static TheoryData<string, string> NumericSpellings() => Spellings(FilterFieldKind.Numeric);
    public static TheoryData<string, string> TextSpellings() => Spellings(FilterFieldKind.Text);
    public static TheoryData<string, string> TagSpellings() => Spellings(FilterFieldKind.Tag);

    private static TheoryData<string, string> Spellings(FilterFieldKind kind)
    {
        var data = new TheoryData<string, string>();
        foreach (string spelling in FilterRuleValidator.OperatorsFor(kind))
        {
            data.Add(kind.ToString(), spelling);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NumericSpellings))]
    public void EveryNumericSpellingBehavesAsMeasured(string kind, string spelling)
    {
        Assert.Equal(FilterFieldKind.Numeric.ToString(), kind);
        Assert.Equal(NumericVectors[spelling], Vector("Close", spelling, "50", "100", "150"));
    }

    [Theory]
    [MemberData(nameof(TextSpellings))]
    public void EveryTextSpellingBehavesAsMeasured(string kind, string spelling)
    {
        Assert.Equal(FilterFieldKind.Text.ToString(), kind);
        Assert.Equal(TextVectors[spelling], Vector("Sector", spelling, "Tech", "Technology", "Energy"));
    }

    [Theory]
    [MemberData(nameof(TagSpellings))]
    public void EveryTagSpellingBehavesAsMeasured(string kind, string spelling)
    {
        Assert.Equal(FilterFieldKind.Tag.ToString(), kind);
        Assert.Equal(TagVectors[spelling], Vector("Tag", spelling, "AI", "AI, Cloud", "Oil"));
    }

    [Fact]
    public void TheTablesListExactlyTheSpellingsOfTheMeasuredBehaviour()
    {
        Assert.Equal(NumericVectors.Keys.OrderBy(k => k, StringComparer.Ordinal), FilterRuleValidator.OperatorsFor(FilterFieldKind.Numeric).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(TextVectors.Keys.OrderBy(k => k, StringComparer.Ordinal), FilterRuleValidator.OperatorsFor(FilterFieldKind.Text).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(TagVectors.Keys.OrderBy(k => k, StringComparer.Ordinal), FilterRuleValidator.OperatorsFor(FilterFieldKind.Tag).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void AnUnknownOperatorIsSilentInTheEvaluator_WhichIsWhyTheValidatorExists()
    {
        // The evaluator never fails on a bad operator: numeric -> no match, text -> "contains", tag -> "contains tag".
        Assert.Equal("FFF", Vector("Close", "BOGUS", "50", "100", "150"));
        Assert.Equal(TextVectors["Contains"], Vector("Sector", "BOGUS", "Tech", "Technology", "Energy"));
        Assert.Equal(TagVectors["Contains"], Vector("Tag", "BOGUS", "AI", "AI, Cloud", "Oil"));
    }

    // ---- field kind -------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Close", FilterFieldKind.Numeric)]
    [InlineData("close", FilterFieldKind.Numeric)]
    [InlineData("Volume", FilterFieldKind.Numeric)]
    [InlineData("ChangePercent", FilterFieldKind.Numeric)]
    [InlineData("TrailingPE", FilterFieldKind.Numeric)]
    [InlineData("Trailing P/E", FilterFieldKind.Numeric)]
    [InlineData("ROE", FilterFieldKind.Numeric)]
    [InlineData("ExDividendDate", FilterFieldKind.Numeric)]
    [InlineData("Sector", FilterFieldKind.Text)]
    [InlineData("Symbol", FilterFieldKind.Text)]
    [InlineData("IsLong", FilterFieldKind.Text)]
    [InlineData("LastUpdatedUtc", FilterFieldKind.Text)]
    [InlineData("Tag", FilterFieldKind.Tag)]
    [InlineData("tag", FilterFieldKind.Tag)]
    public void FieldKind_FollowsTheValueTypeTheEvaluatorSees(string field, FilterFieldKind expected)
    {
        Assert.True(FilterRuleValidator.TryGetFieldKind(field, out FilterFieldKind kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("NoSuchColumn")]
    public void FieldKind_IsUnavailableForAnUnknownName(string? field)
    {
        Assert.False(FilterRuleValidator.TryGetFieldKind(field, out _));
    }

    [Fact]
    public void EveryKnownFieldHasAKind()
    {
        Assert.All(TickerRow.KnownFieldNames, name => Assert.True(FilterRuleValidator.TryGetFieldKind(name, out _), name));
    }

    // ---- Validate ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Close", ">", "100")]
    [InlineData("Close", "  >=  ", "99.5")]
    [InlineData("Close", "GREATERTHAN", "-1")]
    [InlineData("Volume", "<", "1e6")]
    [InlineData("Trailing P/E", "<=", "15")]
    [InlineData("Sector", "==", "Technology")]
    [InlineData("Sector", "Contains", "tech")]
    [InlineData("Sector", "NotContains", "")]
    [InlineData("Tag", "ContainsTag", "AI")]
    [InlineData("Tag", "!=", "Oil")]
    [InlineData("IsLong", "==", "True")]
    public void AMeaningfulRuleHasNoProblem(string field, string op, string value)
    {
        Assert.Null(FilterRuleValidator.Validate(Rule(field, op, value)));
    }

    [Theory]
    [InlineData("NoSuchColumn", ">", "1")]
    [InlineData("", ">", "1")]
    public void AnUnknownFieldIsAFieldProblem(string field, string op, string value)
    {
        FilterRuleProblem? problem = FilterRuleValidator.Validate(Rule(field, op, value));

        Assert.NotNull(problem);
        Assert.Equal(FilterRuleProblemPart.Field, problem.Part);
    }

    [Theory]
    [InlineData("Close", "Contains", "1")]
    [InlineData("Close", "ContainsTag", "1")]
    [InlineData("Close", "", "1")]
    [InlineData("Close", "BOGUS", "1")]
    [InlineData("Sector", ">", "1")]
    [InlineData("Sector", "GreaterThan", "1")]
    [InlineData("Sector", "ContainsTag", "x")]
    [InlineData("Tag", ">", "1")]
    [InlineData("IsLong", ">", "1")]
    [InlineData("LastUpdatedUtc", "<", "2026-01-01")]
    public void AnOperatorTheFieldKindCannotUseIsAnOperatorProblem(string field, string op, string value)
    {
        FilterRuleProblem? problem = FilterRuleValidator.Validate(Rule(field, op, value));

        Assert.NotNull(problem);
        Assert.Equal(FilterRuleProblemPart.Operator, problem.Part);
        Assert.Contains(field, problem.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12 apples")]
    public void ANumericFieldNeedsANumericValue(string value)
    {
        FilterRuleProblem? problem = FilterRuleValidator.Validate(Rule("Close", ">", value));

        Assert.NotNull(problem);
        Assert.Equal(FilterRuleProblemPart.Value, problem.Part);
    }

    [Theory]
    [InlineData("Close", ">", "Open")]
    [InlineData("Volume", ">=", "AverageVolume")]
    [InlineData("Sector", "==", "Industry")]
    [InlineData("Sector", "==", "Tag")]
    [InlineData("Tag", "Contains", "Sector")]
    [InlineData("Tag", "==", "Close")]
    public void ComparingTwoFieldsOfTheSameKindIsFine(string field, string op, string target)
    {
        Assert.Null(FilterRuleValidator.Validate(Rule(field, op, target, compareToField: true)));
    }

    [Theory]
    [InlineData("Close", ">", "Sector")]
    [InlineData("Sector", "==", "Close")]
    [InlineData("Close", ">", "NoSuchColumn")]
    [InlineData("Close", ">", "100")]
    public void ComparingFieldsOfDifferentKindsOrWithAnUnknownFieldIsAValueProblem(string field, string op, string target)
    {
        FilterRuleProblem? problem = FilterRuleValidator.Validate(Rule(field, op, target, compareToField: true));

        Assert.NotNull(problem);
        Assert.Equal(FilterRuleProblemPart.Value, problem.Part);
    }

    [Fact]
    public void TheFirstProblemIsReportedInTheOrderFieldOperatorValue()
    {
        Assert.Equal(FilterRuleProblemPart.Field, FilterRuleValidator.Validate(Rule("Nope", "Contains", "abc"))!.Part);
        Assert.Equal(FilterRuleProblemPart.Operator, FilterRuleValidator.Validate(Rule("Close", "Contains", "abc"))!.Part);
        Assert.Equal(FilterRuleProblemPart.Value, FilterRuleValidator.Validate(Rule("Close", ">", "abc"))!.Part);
    }

    [Fact]
    public void ANullRuleIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => FilterRuleValidator.Validate(null!));
    }
}
