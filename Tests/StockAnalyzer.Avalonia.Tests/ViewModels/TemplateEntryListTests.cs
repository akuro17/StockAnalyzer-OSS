using System;
using System.Linq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class TemplateEntryListTests
{
    private static ColumnTemplate Sentinel() => new() { Id = Guid.Empty, Name = "Active" };
    private static ColumnTemplate Template(string name) => new() { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public void Merge_PutsTheSentinelFirstThenTemplatesInGivenOrder()
    {
        var sentinel = Sentinel();
        var a = Template("A");
        var b = Template("B");

        var result = TemplateEntryList.Merge(sentinel, new[] { b, a }, Guid.Empty, t => t.Id);

        Assert.Equal(new[] { sentinel, b, a }, result.Combined.ToArray());
    }

    [Fact]
    public void Merge_ResolvesThePreviousSelectionToTheNewInstanceWithTheSameId()
    {
        var sentinel = Sentinel();
        var old = Template("A");
        var fresh = new ColumnTemplate { Id = old.Id, Name = "A" };

        var result = TemplateEntryList.Merge(sentinel, new[] { fresh }, old.Id, t => t.Id);

        Assert.Same(fresh, result.Replacement);
        Assert.False(result.SelectedWasDeleted);
    }

    [Fact]
    public void Merge_FallsBackToTheSentinelAndFlagsDeletionWhenTheSelectedTemplateIsGone()
    {
        var sentinel = Sentinel();

        var result = TemplateEntryList.Merge(sentinel, new[] { Template("Other") }, Guid.NewGuid(), t => t.Id);

        Assert.Same(sentinel, result.Replacement);
        Assert.True(result.SelectedWasDeleted);
    }

    [Fact]
    public void Merge_WithNoPreviousTemplateSelected_ResolvesToTheSentinelWithoutDeletion()
    {
        var sentinel = Sentinel();

        var result = TemplateEntryList.Merge(sentinel, new[] { Template("A") }, Guid.Empty, t => t.Id);

        Assert.Same(sentinel, result.Replacement);
        Assert.False(result.SelectedWasDeleted);
    }
}
