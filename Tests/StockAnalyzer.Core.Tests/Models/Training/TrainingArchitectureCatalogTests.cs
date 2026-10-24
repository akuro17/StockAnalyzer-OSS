#nullable enable
using System;
using System.Linq;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>The architecture table moved from the Training Wizard's view model into Core; its content, order and defaults must be exactly what the wizard offered.</summary>
public class TrainingArchitectureCatalogTests
{
    [Theory]
    [InlineData(TrainingFramework.PyTorch, new[] { "lstm", "cnn", "attn_lstm" })]
    [InlineData(TrainingFramework.LightGBM, new[] { "gbdt" })]
    [InlineData(TrainingFramework.TensorFlow, new[] { "lstm", "gru", "cnn" })]
    public void For_IsTheListTheWizardOfferedBeforeTheMove(TrainingFramework framework, string[] expected)
        => Assert.Equal(expected, TrainingArchitectureCatalog.For(framework));

    [Theory]
    [InlineData(TrainingFramework.PyTorch, "lstm")]
    [InlineData(TrainingFramework.LightGBM, "gbdt")]
    [InlineData(TrainingFramework.TensorFlow, "lstm")]
    public void DefaultFor_IsTheDefaultTheWizardStartedWithBeforeTheMove(TrainingFramework framework, string expected)
        => Assert.Equal(expected, TrainingArchitectureCatalog.DefaultFor(framework));

    [Fact]
    public void EveryFrameworkHasANonEmptyDistinctListAndItsDefaultIsOneOfIt()
    {
        foreach (TrainingFramework framework in Enum.GetValues<TrainingFramework>())
        {
            Assert.NotEmpty(TrainingArchitectureCatalog.For(framework));
            Assert.Contains(TrainingArchitectureCatalog.DefaultFor(framework), TrainingArchitectureCatalog.For(framework));
            Assert.Equal(TrainingArchitectureCatalog.For(framework).Distinct(StringComparer.Ordinal).Count(), TrainingArchitectureCatalog.For(framework).Count);
        }
    }

    [Fact]
    public void For_ReturnsTheSameInstanceEveryTimeSoABindingSeesNoChange()
        => Assert.Same(TrainingArchitectureCatalog.For(TrainingFramework.PyTorch), TrainingArchitectureCatalog.For(TrainingFramework.PyTorch));

    [Fact]
    public void AValueWithoutAnEntryIsRefusedNotGuessed()
    {
        var undefined = (TrainingFramework)(-1);

        Assert.Throws<InvalidOperationException>(() => TrainingArchitectureCatalog.For(undefined));
        Assert.Throws<InvalidOperationException>(() => TrainingArchitectureCatalog.DefaultFor(undefined));
    }

    [Fact]
    public void ThePyTorchListEndsWithTheAttentionContractName()
        => Assert.Equal(AttentionContract.Architecture, TrainingArchitectureCatalog.For(TrainingFramework.PyTorch).Last());
}
