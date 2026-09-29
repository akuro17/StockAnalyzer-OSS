using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Avalonia.ViewModels.Spikes;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class SpikeGridSourceTests
{
    private List<SpikeItem> GetTestItems()
    {
        return new List<SpikeItem>
        {
            new SpikeItem { Id = 1, Name = "Item A", Value = 100, Category = "X" },
            new SpikeItem { Id = 2, Name = "Item B", Value = 50, Category = "Y" },
            new SpikeItem { Id = 3, Name = "Item C", Value = 200, Category = "X" }
        };
    }

    [Fact]
    public void ShouldInitializeWithItems()
    {
        var vm = new SpikeGridSourceViewModel(GetTestItems());
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public void ShouldSortByValueAscending()
    {
        var vm = new SpikeGridSourceViewModel(GetTestItems());
        
        vm.Sort(nameof(SpikeItem.Value), true);
        
        Assert.Equal(50, vm.Items[0].Value);
        Assert.Equal(100, vm.Items[1].Value);
        Assert.Equal(200, vm.Items[2].Value);
    }

    [Fact]
    public void ShouldSortByValueDescending()
    {
        var vm = new SpikeGridSourceViewModel(GetTestItems());
        
        vm.Sort(nameof(SpikeItem.Value), false);
        
        Assert.Equal(200, vm.Items[0].Value);
        Assert.Equal(100, vm.Items[1].Value);
        Assert.Equal(50, vm.Items[2].Value);
    }

    [Fact]
    public void ShouldFilterByCategory()
    {
        var vm = new SpikeGridSourceViewModel(GetTestItems());
        
        vm.Filter("X");
        
        Assert.Equal(2, vm.Items.Count);
        Assert.All(vm.Items, item => Assert.Equal("X", item.Category));
    }
}
