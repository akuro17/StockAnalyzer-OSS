using System.Globalization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

public class SqlConditionTranslatorTests
{
    [Fact]
    public void Translate_RsiCondition_GeneratesSingleDivisionFormulaWithGuard()
    {
        // Arrange
        var condition = new RsiOversoldCondition(14, 30.5m);
        var criteria = new ScreeningCriteria
        {
            Conditions = [condition]
        };

        // Act
        string sql = SqlConditionTranslator.Translate(criteria);

        // Assert
        Assert.NotNull(sql);
        // Single-division algebraic formula: 100.0 * ag / (ag + al)
        Assert.Contains("WHEN (ag + al) <= 1e-12 THEN 50.0", sql);
        Assert.Contains("ELSE (100.0 * ag) / (ag + al)", sql);
        // Correct time-series latest-bar ordering and warmup cutoff
        Assert.Contains("WHERE rn > 14", sql);
        Assert.Contains("ORDER BY rn DESC", sql);
        Assert.Contains("LIMIT 1", sql);
        // Culture-invariant decimal formatting
        Assert.Contains("< 30.5", sql);
    }

    [Fact]
    public void Translate_EmptyCriteria_ReturnsMatchAll()
    {
        // Arrange
        var criteria = new ScreeningCriteria();

        // Act
        string sql = SqlConditionTranslator.Translate(criteria);

        // Assert
        Assert.Equal("1=1", sql);
    }
}
