using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Watchlist;
using Xunit;

namespace StockAnalyzer.Core.Tests.Watchlist;

public class FilterRuleEvaluatorTests
{
    private sealed class DummyFilterable : IFilterableSymbol
    {
        public string Symbol { get; init; } = "TEST";
        public string? Tag { get; init; }
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object? GetPropertyValue(string propertyName)
        {
            if (string.Equals(propertyName, nameof(Symbol), StringComparison.OrdinalIgnoreCase)) return Symbol;
            if (string.Equals(propertyName, nameof(Tag), StringComparison.OrdinalIgnoreCase)) return Tag;
            return Properties.TryGetValue(propertyName, out var val) ? val : null;
        }
    }

    [Fact]
    public void Evaluate_NullItem_ReturnsFalse()
    {
        Assert.False(FilterRuleEvaluator.Evaluate(null!, new FilterSettings()));
    }

    [Fact]
    public void Evaluate_NullOrEmptySettings_ReturnsTrue()
    {
        var item = new DummyFilterable();
        Assert.True(FilterRuleEvaluator.Evaluate(item, null));
        Assert.True(FilterRuleEvaluator.Evaluate(item, new FilterSettings { Rules = new() }));
    }

    [Fact]
    public void Evaluate_AllRulesMustMatch_AndLogic()
    {
        var item = new DummyFilterable();
        item.Properties["Close"] = 100m;
        item.Properties["Volume"] = 5000L;

        var settings = new FilterSettings
        {
            Rules = new()
            {
                new FilterRule { Field = "Close", Operator = ">", Value = "50" },
                new FilterRule { Field = "Volume", Operator = ">=", Value = "1000" }
            }
        };

        Assert.True(FilterRuleEvaluator.Evaluate(item, settings));

        settings.Rules.Add(new FilterRule { Field = "Close", Operator = "<", Value = "80" });
        Assert.False(FilterRuleEvaluator.Evaluate(item, settings));
    }

    [Theory]
    [InlineData("==", "100", "100", true)]
    [InlineData("=", "100", "100", true)]
    [InlineData("Equals", "100", "100", true)]
    [InlineData("==", "100", "101", false)]
    [InlineData("!=", "100", "101", true)]
    [InlineData("NotEquals", "100", "100", false)]
    [InlineData(">", "100", "50", true)]
    [InlineData(">", "100", "100", false)]
    [InlineData("GreaterThan", "100", "99.99", true)]
    [InlineData(">=", "100", "100", true)]
    [InlineData("GreaterThanOrEqual", "100", "101", false)]
    [InlineData("<", "50", "100", true)]
    [InlineData("LessThan", "100", "100", false)]
    [InlineData("<=", "100", "100", true)]
    [InlineData("LessThanOrEqual", "100", "99", false)]
    [InlineData("UnknownOp", "100", "100", false)]
    public void NumericOperators_EvaluateCorrectly(string op, string itemVal, string ruleVal, bool expected)
    {
        var item = new DummyFilterable();
        item.Properties["Metric"] = decimal.Parse(itemVal);

        var rule = new FilterRule { Field = "Metric", Operator = op, Value = ruleVal };
        Assert.Equal(expected, FilterRuleEvaluator.EvaluateRule(item, rule));
    }

    [Theory]
    [InlineData("*=", "Technology", "Tech", true)]
    [InlineData("Contains", "Technology", "tech", true)]
    [InlineData("", "Technology", "nolo", true)]
    [InlineData("!*=", "Technology", "Bio", true)]
    [InlineData("NotContains", "Technology", "Tech", false)]
    [InlineData("!Contains", "Technology", "Tech", false)]
    [InlineData("==", "Tech", "tech", true)]
    [InlineData("=", "Tech", "tech", true)]
    [InlineData("Equals", "Tech", "Tech", true)]
    [InlineData("!=", "Tech", "Bio", true)]
    [InlineData("NotEquals", "Tech", "tech", false)]
    [InlineData("UnknownOp", "Technology", "tech", true)] // defaults to Contains
    public void TextOperators_EvaluateCorrectly(string op, string itemVal, string ruleVal, bool expected)
    {
        var item = new DummyFilterable();
        item.Properties["Sector"] = itemVal;

        var rule = new FilterRule { Field = "Sector", Operator = op, Value = ruleVal };
        Assert.Equal(expected, FilterRuleEvaluator.EvaluateRule(item, rule));
    }

    [Theory]
    [InlineData("Tech, Core", "Tech", "Contains", true)]
    [InlineData("Tech, Core", "core", "*=", true)]
    [InlineData("Tech, Core", "Semiconductor", "ContainsTag", false)]
    [InlineData("Tech, Core", "Semiconductor", "!*=", true)]
    [InlineData("Tech, Core", "Tech", "NotContains", false)]
    [InlineData("Tech, Core", "Tech", "!Contains", false)]
    [InlineData("Tech", " tech ", "==", true)]
    [InlineData("Tech", "Tech", "=", true)]
    [InlineData("Tech", "Tech", "Equals", true)]
    [InlineData("Tech", "Bio", "!=", true)]
    [InlineData("Tech", "Tech", "NotEquals", false)]
    public void TagField_EvaluatesCorrectly(string tags, string ruleVal, string op, bool expected)
    {
        var item = new DummyFilterable { Tag = tags };
        var rule = new FilterRule { Field = "Tag", Operator = op, Value = ruleVal };
        Assert.Equal(expected, FilterRuleEvaluator.EvaluateRule(item, rule));
    }

    [Fact]
    public void CompareToField_Numeric_EvaluatesCorrectly()
    {
        var item = new DummyFilterable();
        item.Properties["Close"] = 120m;
        item.Properties["TargetPrice"] = 110m;

        var rule = new FilterRule
        {
            Field = "Close",
            Operator = ">",
            Value = "TargetPrice",
            IsCompareToField = true
        };

        Assert.True(FilterRuleEvaluator.EvaluateRule(item, rule));

        item.Properties["Close"] = 100m;
        Assert.False(FilterRuleEvaluator.EvaluateRule(item, rule));
    }

    [Fact]
    public void CompareToField_MissingField_ReturnsFalse()
    {
        var item = new DummyFilterable();
        item.Properties["Close"] = 120m;

        var rule = new FilterRule
        {
            Field = "Close",
            Operator = ">",
            Value = "NonExistentField",
            IsCompareToField = true
        };

        Assert.False(FilterRuleEvaluator.EvaluateRule(item, rule));
    }

    [Fact]
    public void LiteralNumeric_UnparsableValue_ReturnsFalse()
    {
        var item = new DummyFilterable();
        item.Properties["Close"] = 120m;

        var rule = new FilterRule
        {
            Field = "Close",
            Operator = ">",
            Value = "NotANumber",
            IsCompareToField = false
        };

        Assert.False(FilterRuleEvaluator.EvaluateRule(item, rule));
    }
}
