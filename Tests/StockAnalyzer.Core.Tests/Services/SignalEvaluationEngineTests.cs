using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class SignalEvaluationEngineTests
{
    [Fact]
    public void EvaluateEntry_NumericComparison_ShouldReturnTrueWhenConditionMet()
    {
        // Arrange
        var candles = new List<CandleData>
        {
            new CandleData(DateTime.UtcNow.AddDays(-2), 100m, 105m, 95m, 100m, 1000),
            new CandleData(DateTime.UtcNow.AddDays(-1), 100m, 110m, 99m, 105m, 1200),
            new CandleData(DateTime.UtcNow, 105m, 120m, 104m, 115m, 1500)
        };

        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                OutputName = "Close"
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 110m,
            IsEnabled = true
        };

        // Act
        bool result = SignalEvaluationEngine.EvaluateEntry(entry, candles);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void EvaluateBundle_MultipleConditions_ShouldReturnTrueOnlyWhenAllMet()
    {
        // Arrange
        var candles = new List<CandleData>
        {
            new CandleData(DateTime.UtcNow.AddDays(-2), 100m, 105m, 95m, 100m, 1000),
            new CandleData(DateTime.UtcNow.AddDays(-1), 100m, 110m, 99m, 105m, 1200),
            new CandleData(DateTime.UtcNow, 105m, 120m, 104m, 115m, 1500)
        };

        var entry1 = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Close" },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 110m,
            IsEnabled = true
        };

        var entry2 = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Volume" },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 1000,
            IsEnabled = true
        };

        var bundle = new BundledSignalCondition("Test Bundle", SignalTargetType.Long, new[] { entry1, entry2 });

        // Act
        bool result = SignalEvaluationEngine.EvaluateBundle(bundle, candles);

        // Assert
        Assert.True(result);
    }
}
