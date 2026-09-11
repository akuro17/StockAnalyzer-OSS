using System;
using Xunit;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class ColumnItemViewModelTests
{
    [Fact]
    public void Create_WithKnownMetadata_MapsCorrectProperties()
    {
        // Arrange
        var metadata = new WatchlistColumnMetadata("Col_ReturnOnEquity", "ReturnOnEquity");

        // Act
        var sut = ColumnItemViewModel.Create(metadata, true);

        // Assert
        Assert.Equal("ReturnOnEquity", sut.MemberName);
        Assert.Equal("Col_ReturnOnEquity", sut.HeaderKey);
        Assert.True(sut.IsActive);
        Assert.Equal(ColumnCategory.Ratio, sut.Category);
        Assert.Equal("ROE", sut.EnglishName);
        Assert.Contains("Return on Equity", sut.Description);
        Assert.Contains("Equity", sut.Formula);
        Assert.False(sut.IsSymbol);
        Assert.False(sut.IsSelect);
    }

    [Fact]
    public void Create_WithUnknownMetadata_FallsBackGracefully()
    {
        // Arrange
        var metadata = new WatchlistColumnMetadata("Col_UnknownIndicator", "UnknownIndicator");

        // Act
        var sut = ColumnItemViewModel.Create(metadata, false);

        // Assert
        Assert.Equal("UnknownIndicator", sut.MemberName);
        Assert.Equal("Col_UnknownIndicator", sut.HeaderKey);
        Assert.False(sut.IsActive);
        Assert.Equal(ColumnCategory.Basic, sut.Category);
        Assert.Equal("UnknownIndicator", sut.EnglishName);
        Assert.Contains("UnknownIndicator", sut.Description);
    }

    [Theory]
    [InlineData("Symbol", true, false)]
    [InlineData("IsChecked", false, true)]
    [InlineData("Close", false, false)]
    public void SpecialColumns_ReturnCorrectBooleanFlags(string memberName, bool expectedIsSymbol, bool expectedIsSelect)
    {
        // Arrange
        var metadata = new WatchlistColumnMetadata($"Col_{memberName}", memberName);

        // Act
        var sut = ColumnItemViewModel.Create(metadata, true);

        // Assert
        Assert.Equal(expectedIsSymbol, sut.IsSymbol);
        Assert.Equal(expectedIsSelect, sut.IsSelect);
    }

    [Fact]
    public void IsActive_PropertyChange_NotifiesSubscribers()
    {
        // Arrange
        var metadata = new WatchlistColumnMetadata("Col_Close", "Close");
        var sut = ColumnItemViewModel.Create(metadata, false);
        bool fired = false;
        sut.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ColumnItemViewModel.IsActive))
            {
                fired = true;
            }
        };

        // Act
        sut.IsActive = true;

        // Assert
        Assert.True(fired);
        Assert.True(sut.IsActive);
    }
}
