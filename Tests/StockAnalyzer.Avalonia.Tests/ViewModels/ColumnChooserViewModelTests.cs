using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class ColumnChooserViewModelTests
{
    private readonly List<WatchlistColumnMetadata> _allColumns = new()
    {
        new WatchlistColumnMetadata("Col_Symbol", "Symbol"),
        new WatchlistColumnMetadata("Col_Name", "Name"),
        new WatchlistColumnMetadata("Col_ReturnOnEquity", "ReturnOnEquity"),
        new WatchlistColumnMetadata("Col_ReturnOnAssets", "ReturnOnAssets"),
        new WatchlistColumnMetadata("Col_Ebitda", "Ebitda"),
        new WatchlistColumnMetadata("Col_FreeCashflow", "FreeCashflow"),
        new WatchlistColumnMetadata("Col_TrailingPE", "TrailingPE")
    };

    [Fact]
    public void Constructor_InitializesItemsAndFiltersByActiveCategory()
    {
        // Arrange
        var activeColumns = new[] { "Symbol", "ReturnOnEquity" };

        // Act
        var sut = new ColumnChooserViewModel(_allColumns, activeColumns);

        // Assert
        Assert.Equal(7, sut.AllItems.Count);
        // By default, category is Active.
        Assert.Equal(2, sut.FilteredItems.Count);
        Assert.Contains(sut.FilteredItems, x => x.MemberName == "Symbol" && x.IsActive);
        Assert.Contains(sut.FilteredItems, x => x.MemberName == "ReturnOnEquity" && x.IsActive);
    }

    [Fact]
    public void Constructor_PreservesCustomActiveColumnsOrder()
    {
        // Arrange
        var customOrder = new[] { "ReturnOnEquity", "Symbol", "TrailingPE" };

        // Act
        var sut = new ColumnChooserViewModel(_allColumns, customOrder);

        // Assert: FilteredItems (which represents Active category by default) must be in exactly the customOrder sequence
        Assert.Equal(3, sut.FilteredItems.Count);
        Assert.Equal("ReturnOnEquity", sut.FilteredItems[0].MemberName);
        Assert.Equal("Symbol", sut.FilteredItems[1].MemberName);
        Assert.Equal("TrailingPE", sut.FilteredItems[2].MemberName);
    }

    [Fact]
    public void ChangingCategory_FiltersItemsCorrectly()
    {
        // Arrange
        var sut = new ColumnChooserViewModel(_allColumns, Array.Empty<string>());

        // Act & Assert (Ratio Category)
        sut.SelectedCategory = ColumnCategory.Ratio;
        Assert.Equal(2, sut.FilteredItems.Count); // ReturnOnEquity, ReturnOnAssets
        Assert.Contains(sut.FilteredItems, x => x.MemberName == "ReturnOnEquity");
        Assert.Contains(sut.FilteredItems, x => x.MemberName == "ReturnOnAssets");

        // Act & Assert (Financial Category)
        sut.SelectedCategory = ColumnCategory.Financial;
        Assert.Equal(2, sut.FilteredItems.Count); // Ebitda, FreeCashflow
        Assert.Contains(sut.FilteredItems, x => x.MemberName == "Ebitda");
    }

    [Fact]
    public void SearchQuery_FiltersFilteredItems()
    {
        // Arrange
        var sut = new ColumnChooserViewModel(_allColumns, Array.Empty<string>())
        {
            SelectedCategory = ColumnCategory.Ratio
        };

        // Act & Assert: Search for "ROE" or part of English description
        sut.SearchQuery = "equity";
        Assert.Single(sut.FilteredItems);
        Assert.Equal("ReturnOnEquity", sut.FilteredItems[0].MemberName);

        // Search using "profitability"
        sut.SearchQuery = "profitability";
        Assert.Single(sut.FilteredItems);
        Assert.Equal("ReturnOnEquity", sut.FilteredItems[0].MemberName);

        // Clear query
        sut.SearchQuery = "";
        Assert.Equal(2, sut.FilteredItems.Count);
    }

    [Fact]
    public void SelectAllCommand_ActivatesAllExceptSymbol()
    {
        // Arrange
        var active = new[] { "Symbol" };
        var sut = new ColumnChooserViewModel(_allColumns, active);
        sut.SelectedCategory = ColumnCategory.Basic;

        // Act
        sut.SelectAllCommand.Execute(null);

        // Assert: Category is Basic. Symbol is already active. Name (Basic) should become active.
        var nameItem = sut.AllItems.First(x => x.MemberName == "Name");
        Assert.True(nameItem.IsActive);

        // Ratio Category items should NOT be affected
        var roeItem = sut.AllItems.First(x => x.MemberName == "ReturnOnEquity");
        Assert.False(roeItem.IsActive);
    }

    [Fact]
    public void ClearAllCommand_DeactivatesAllExceptSymbol()
    {
        // Arrange
        var active = new[] { "Symbol", "Name" };
        var sut = new ColumnChooserViewModel(_allColumns, active);
        sut.SelectedCategory = ColumnCategory.Basic;

        // Act
        sut.ClearAllCommand.Execute(null);

        // Assert: Name (Basic) is deactivated, Symbol remains active.
        var symbolItem = sut.AllItems.First(x => x.MemberName == "Symbol");
        var nameItem = sut.AllItems.First(x => x.MemberName == "Name");
        Assert.True(symbolItem.IsActive);
        Assert.False(nameItem.IsActive);
    }

    [Fact]
    public void GetActiveColumnNames_ReturnsOnlyActiveNames()
    {
        // Arrange
        var active = new[] { "Symbol", "ReturnOnEquity" };
        var sut = new ColumnChooserViewModel(_allColumns, active);

        // Act
        var result = sut.GetActiveColumnNames();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("Symbol", result);
        Assert.Contains("ReturnOnEquity", result);
    }

    [Fact]
    public void AllItemsMove_ReordersFilteredItems()
    {
        // Arrange
        var active = new[] { "Symbol", "Name" };
        var sut = new ColumnChooserViewModel(_allColumns, active);
        sut.SelectedCategory = ColumnCategory.Active;
        // Initial Active category items: Symbol (0), Name (1)
        Assert.Equal("Symbol", sut.FilteredItems[0].MemberName);
        Assert.Equal("Name", sut.FilteredItems[1].MemberName);

        // Act: Move Symbol to index 1 (swapping with Name in AllItems)
        int symbolIndex = sut.AllItems.IndexOf(sut.AllItems.First(x => x.MemberName == "Symbol"));
        int nameIndex = sut.AllItems.IndexOf(sut.AllItems.First(x => x.MemberName == "Name"));
        sut.AllItems.Move(symbolIndex, nameIndex);

        // Assert: FilteredItems order should now reflect Name first, then Symbol
        Assert.Equal("Name", sut.FilteredItems[0].MemberName);
        Assert.Equal("Symbol", sut.FilteredItems[1].MemberName);
    }

    [Fact]
    public void ChangingCategory_All_FiltersAllItems()
    {
        // Arrange
        var sut = new ColumnChooserViewModel(_allColumns, Array.Empty<string>());

        // Act
        sut.SelectedCategory = ColumnCategory.All;

        // Assert
        Assert.Equal(7, sut.FilteredItems.Count);
    }

    [Fact]
    public void ChangingCategory_SortsItemsAlphabetically()
    {
        // Arrange
        var sut = new ColumnChooserViewModel(_allColumns, Array.Empty<string>());

        // Act
        sut.SelectedCategory = ColumnCategory.All;

        // Assert: Filtered items should be sorted by Select/Symbol first, then EnglishName alphabetically across all categories.
        Assert.Equal(7, sut.FilteredItems.Count);
        Assert.Equal("Symbol", sut.FilteredItems[0].MemberName);
        Assert.Equal("Ebitda", sut.FilteredItems[1].MemberName);
        Assert.Equal("FreeCashflow", sut.FilteredItems[2].MemberName);
        Assert.Equal("Name", sut.FilteredItems[3].MemberName);
        Assert.Equal("ReturnOnAssets", sut.FilteredItems[4].MemberName);
        Assert.Equal("ReturnOnEquity", sut.FilteredItems[5].MemberName);
        Assert.Equal("TrailingPE", sut.FilteredItems[6].MemberName);
    }

    [Fact]
    public void Templates_Save_Load_Delete_Operations()
    {
        // Arrange
        var activeColumns = new[] { "Symbol", "ReturnOnEquity", "TrailingPE" };
        var sut = new ColumnChooserViewModel(_allColumns, activeColumns);
        
        // Clean existing templates before test if file exists
        sut.Templates.Clear();
        sut.NewTemplateName = "TestTemplate";

        // Act: Save template
        sut.SaveTemplateCommand.Execute(null);

        // Assert: Template added
        Assert.Single(sut.Templates);
        Assert.Equal("TestTemplate", sut.Templates[0].Name);
        Assert.Equal(3, sut.Templates[0].ColumnNames.Count);

        // Act: Change active columns
        foreach (var item in sut.AllItems)
        {
            if (item.MemberName != "Symbol") item.IsActive = false;
        }
        Assert.Single(sut.GetActiveColumnNames());

        // Act: Load template
        sut.LoadTemplateCommand.Execute(sut.Templates[0]);

        // Assert: Restored
        var activeNow = sut.GetActiveColumnNames();
        Assert.Equal(3, activeNow.Count);
        Assert.Contains("Symbol", activeNow);
        Assert.Contains("ReturnOnEquity", activeNow);
        Assert.Contains("TrailingPE", activeNow);

        // Act: Check Right Column Alphabetical Display
        sut.SelectedTemplate = sut.Templates[0];
        Assert.Equal(3, sut.SelectedTemplateColumnNames.Count);
        Assert.Equal("ROE", sut.SelectedTemplateColumnNames[0]);
        Assert.Equal("Symbol", sut.SelectedTemplateColumnNames[1]);
        Assert.Equal("Trailing P/E", sut.SelectedTemplateColumnNames[2]);

        // Act: Delete template
        sut.DeleteTemplateCommand.Execute(sut.Templates[0]);

        // Assert: Removed
        Assert.Empty(sut.Templates);
        Assert.Null(sut.SelectedTemplate);
    }
}
