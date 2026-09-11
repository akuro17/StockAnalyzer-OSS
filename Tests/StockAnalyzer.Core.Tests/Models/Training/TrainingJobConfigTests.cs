using System;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// Range coverage for <see cref="TrainingJobConfig.Validate"/>, focused on the walk-forward and
/// out-of-sample controls (<see cref="TrainingJobConfig.NSplits"/> / <see cref="TrainingJobConfig.Gap"/>
/// / <see cref="TrainingJobConfig.OosTailDays"/>) added with the validation and target-definition
/// feature. The pre-existing required-cardinality checks are exercised through
/// <see cref="StockAnalyzer.Core.Services.TrainingOrchestrator"/> tests.
/// </summary>
public class TrainingJobConfigTests
{
    private static TrainingJobConfig Valid() => new()
    {
        Symbols = new[] { "7203-T" },
        Architecture = "lstm",
        WindowSize = 60,
        Horizon = 5,
    };

    [Fact]
    public void Validate_DefaultWalkForwardAndOosControls_DoesNotThrow()
    {
        var config = Valid();

        var exception = Record.Exception(config.Validate);

        Assert.Null(exception);
        Assert.Equal(TargetType.Classification, config.TargetType);
        Assert.Equal(WalkForwardDataRequirement.DefaultSplitCount, config.NSplits);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    public void Validate_NSplitsAtLeastTwo_DoesNotThrow(int nSplits)
    {
        var config = Valid() with { NSplits = nSplits };

        Assert.Null(Record.Exception(config.Validate));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    public void Validate_NSplitsBelowTwo_Throws(int nSplits)
    {
        var config = Valid() with { NSplits = nSplits };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Validate_NegativeGap_Throws()
    {
        var config = Valid() with { Gap = -1 };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Validate_ZeroGap_DoesNotThrow()
    {
        var config = Valid() with { Gap = 0 };

        Assert.Null(Record.Exception(config.Validate));
    }

    [Fact]
    public void Validate_NegativeOosTailDays_Throws()
    {
        var config = Valid() with { OosTailDays = -1 };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Validate_NonNegativeOosTailDays_DoesNotThrow()
    {
        var config = Valid() with { OosTailDays = 0, Gap = 7 };

        Assert.Null(Record.Exception(config.Validate));
    }
}
