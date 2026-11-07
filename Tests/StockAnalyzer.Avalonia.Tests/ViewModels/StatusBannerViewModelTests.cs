using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Moq;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>D-3' Run C / W6: the banner row's view model (pure collection logic, no parent reference).</summary>
public class StatusBannerViewModelTests
{
    private static StatusBannerItem Item(string id, string text = "text", StatusBannerSeverity severity = StatusBannerSeverity.Warning) =>
        new(id, severity, text, Array.Empty<StatusBannerAction>());

    [Fact]
    public void New_HasNoItems()
    {
        var banner = new StatusBannerViewModel();

        Assert.Empty(banner.Items);
        Assert.False(banner.HasItems);
    }

    [Fact]
    public void Show_SeveralDifferentIds_ShowsThemAllInOrder()
    {
        var banner = new StatusBannerViewModel();

        banner.Show(Item("a"));
        banner.Show(Item("b"));
        banner.Show(Item("c"));

        Assert.Equal(new[] { "a", "b", "c" }, banner.Items.Select(i => i.Id));
        Assert.True(banner.HasItems);
    }

    [Fact]
    public void Show_SameId_ReplacesInPlaceInsteadOfStacking()
    {
        var banner = new StatusBannerViewModel();
        banner.Show(Item("a", "first"));
        banner.Show(Item("b"));

        banner.Show(Item("a", "second"));

        Assert.Equal(new[] { "a", "b" }, banner.Items.Select(i => i.Id));
        Assert.Equal("second", banner.Items[0].Text);
    }

    [Fact]
    public void Remove_RemovesOnlyTheNamedItem()
    {
        var banner = new StatusBannerViewModel();
        banner.Show(Item("a"));
        banner.Show(Item("b"));

        Assert.True(banner.Remove("a"));

        Assert.Equal("b", Assert.Single(banner.Items).Id);
        Assert.True(banner.HasItems);
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse()
    {
        var banner = new StatusBannerViewModel();
        banner.Show(Item("a"));

        Assert.False(banner.Remove("zzz"));
        Assert.Single(banner.Items);
    }

    [Fact]
    public void Remove_LastItem_ClearsHasItemsAndAnnounces()
    {
        var banner = new StatusBannerViewModel();
        banner.Show(Item("a"));
        var changed = new List<string?>();
        banner.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        banner.Remove("a");

        Assert.False(banner.HasItems);
        Assert.Contains(nameof(StatusBannerViewModel.HasItems), changed);
    }

    [Fact]
    public void Show_AnnouncesHasItemsWhenTheFirstItemArrives()
    {
        var banner = new StatusBannerViewModel();
        var changed = new List<string?>();
        banner.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        banner.Show(Item("a"));

        Assert.Contains(nameof(StatusBannerViewModel.HasItems), changed);
    }

    [Fact]
    public void Show_NullItem_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusBannerViewModel().Show(null!));
    }

    [Fact]
    public void Ids_AreComparedExactly()
    {
        var banner = new StatusBannerViewModel();
        banner.Show(Item("Workspace"));
        banner.Show(Item("workspace"));

        Assert.Equal(2, banner.Items.Count);
    }

    [Fact]
    public void Item_IsError_FollowsSeverity()
    {
        Assert.True(Item("a", severity: StatusBannerSeverity.Error).IsError);
        Assert.False(Item("a", severity: StatusBannerSeverity.Warning).IsError);
    }

    [Fact]
    public void Action_CarriesTheCommandItWasGiven()
    {
        var command = new Mock<ICommand>().Object;
        var item = new StatusBannerItem("a", StatusBannerSeverity.Warning, "t", new[] { new StatusBannerAction("Go", command) });

        Assert.Same(command, Assert.Single(item.Actions).Command);
        Assert.Equal("Go", item.Actions[0].Text);
    }
}
