using System.Collections.Generic;
using StockAnalyzer.Core.Models.Semantic;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.Analysis;

public class SignalConflictRegistryTests
{
    // A simple test implementation of ISemanticSignal
    private readonly record struct TestSignal(int Index, int Priority, SemanticRole Role, string Name) : ISemanticSignal;

    [Fact]
    public void ResolveConflicts_ReturnsHighestPrioritySignal_WhenIndexesMatch()
    {
        // Arrange
        var registry = new SignalConflictRegistry<TestSignal>();
        var signals = new List<TestSignal>
        {
            new TestSignal(10, 1, SemanticRole.Support, "LowPriority"),
            new TestSignal(10, 5, SemanticRole.Support, "HighPriority"),
            new TestSignal(10, 3, SemanticRole.Support, "MidPriority")
        };
        var outputBuffer = new List<TestSignal>();

        // Act
        registry.ResolveConflicts(signals, outputBuffer);

        // Assert
        Assert.Single(outputBuffer);
        Assert.Equal("HighPriority", outputBuffer[0].Name);
    }

    [Fact]
    public void ResolveConflicts_AppliesCustomExclusionRules()
    {
        // Arrange
        var registry = new SignalConflictRegistry<TestSignal>();
        
        // Rule: If an existing signal is Support, reject any new Resistance signal that occurs within 5 candles.
        registry.AddExclusionRule((existing, candidate) => 
            existing.Role == SemanticRole.Support &&
            candidate.Role == SemanticRole.Resistance &&
            (candidate.Index - existing.Index) <= 5);

        var signals = new List<TestSignal>
        {
            new TestSignal(10, 1, SemanticRole.Support, "Support1"),
            new TestSignal(12, 1, SemanticRole.Resistance, "ResistanceFiltered"), // Delta = 2, should be excluded
            new TestSignal(16, 1, SemanticRole.Resistance, "ResistanceKept")      // Delta = 6, should be kept
        };
        var outputBuffer = new List<TestSignal>();

        // Act
        registry.ResolveConflicts(signals, outputBuffer);

        // Assert
        Assert.Equal(2, outputBuffer.Count);
        Assert.Equal("Support1", outputBuffer[0].Name);
        Assert.Equal("ResistanceKept", outputBuffer[1].Name);
    }

    [Fact]
    public void ResolveConflicts_KeepsMultipleValid_WhenNoConflictsOccur()
    {
        // Arrange
        var registry = new SignalConflictRegistry<TestSignal>();
        var signals = new List<TestSignal>
        {
            new TestSignal(0, 1, SemanticRole.Support, "Sig1"),
            new TestSignal(10, 1, SemanticRole.Support, "Sig2"),
            new TestSignal(20, 1, SemanticRole.Support, "Sig3")
        };
        var outputBuffer = new List<TestSignal>();

        // Act
        registry.ResolveConflicts(signals, outputBuffer);

        // Assert
        Assert.Equal(3, outputBuffer.Count);
        Assert.Equal(0, outputBuffer[0].Index);
        Assert.Equal(10, outputBuffer[1].Index);
        Assert.Equal(20, outputBuffer[2].Index);
    }

    [Fact]
    public void ResolveConflicts_OperatesThroughZeroAllocationBuffer()
    {
        // Arrange
        var registry = new SignalConflictRegistry<TestSignal>();
        var signals = new List<TestSignal>
        {
            new TestSignal(5, 1, SemanticRole.Support, "Sig1"),
            new TestSignal(15, 1, SemanticRole.Resistance, "Sig2")
        };
        var outputBuffer = new List<TestSignal>();
        
        // Populate buffer with garbage to test that Clear() happens properly
        outputBuffer.Add(new TestSignal(999, 999, SemanticRole.None, "Garbage"));

        // Act
        registry.ResolveConflicts(signals, outputBuffer);

        // Assert
        Assert.Equal(2, outputBuffer.Count);
        Assert.DoesNotContain(outputBuffer, s => s.Name == "Garbage");
        Assert.Equal("Sig1", outputBuffer[0].Name);
    }
}
