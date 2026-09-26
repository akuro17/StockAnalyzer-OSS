using System;
using Avalonia.Data;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class MinimalFixAuditTests
{
    [Fact]
    public void DecimalToIntConverter_HandlesOverflow_WithoutThrowing()
    {
        var converter = new DecimalToIntConverter();

        // Convert (double/float -> decimal)
        var extremeDoubleHigh = converter.Convert((double)1e38, typeof(decimal), null, null);
        var extremeDoubleLow = converter.Convert((double)(-1e38), typeof(decimal), null, null);
        var extremeFloatHigh = converter.Convert((float)1e38, typeof(decimal), null, null);
        var extremeFloatLow = converter.Convert((float)(-1e38), typeof(decimal), null, null);
        var nanDouble = converter.Convert(double.NaN, typeof(decimal), null, null);
        var infinityDouble = converter.Convert(double.PositiveInfinity, typeof(decimal), null, null);

        Assert.Equal(0m, extremeDoubleHigh);
        Assert.Equal(0m, extremeDoubleLow);
        Assert.Equal(0m, extremeFloatHigh);
        Assert.Equal(0m, extremeFloatLow);
        Assert.Equal(0m, nanDouble);
        Assert.Equal(0m, infinityDouble);

        // ConvertBack (UI -> Model)
        var extremeDoubleBack = converter.ConvertBack((double)1e38, typeof(int), null, null);
        Assert.Equal(BindingNotification.UnsetValue, extremeDoubleBack);
    }

    [Fact]
    public void IndicatorRegistrationViewModel_CanRegisterIndicator_ValidatesPreconditions()
    {
        var vm = new IndicatorRegistrationViewModel();

        // Initially LeftSelectedIndicator is set by default if catalog is non-empty or null
        // Case 1: Left is null -> Cannot register
        vm.LeftSelectedIndicator = null;
        Assert.False(vm.RegisterIndicatorCommand.CanExecute(null));

        // Case 2: Left is set, RightTargetMode = NumericValue -> Can register
        vm.LeftSelectedIndicator = new ScreenerCatalogItem { DisplayName = "SMA" };
        vm.RightTargetMode = RightHandTargetMode.NumericValue;
        Assert.True(vm.RegisterIndicatorCommand.CanExecute(null));

        // Case 3: RightTargetMode = Indicator, RightSelectedIndicator = null -> Cannot register
        vm.SetRightModeIndicatorCommand.Execute(null);
        vm.RightSelectedIndicator = null;
        Assert.False(vm.RegisterIndicatorCommand.CanExecute(null));

        // Case 4: RightTargetMode = Indicator, RightSelectedIndicator set -> Can register
        vm.RightSelectedIndicator = new ScreenerCatalogItem { DisplayName = "RSI" };
        Assert.True(vm.RegisterIndicatorCommand.CanExecute(null));
    }

    [Fact]
    public void IndicatorRegistrationViewModel_SetRightModeNumeric_ClearsStaleIndicatorState()
    {
        var vm = new IndicatorRegistrationViewModel();
        vm.RightSelectedIndicator = new ScreenerCatalogItem { DisplayName = "RSI" };
        vm.RightAvailableOutputs.Add("Main");

        // Act
        vm.SetRightModeNumericCommand.Execute(null);

        // Assert
        Assert.Equal(RightHandTargetMode.NumericValue, vm.RightTargetMode);
        Assert.Null(vm.RightSelectedIndicator);
        Assert.Null(vm.RightIndicatorSettings);
        Assert.Empty(vm.RightAvailableOutputs);
    }

    [Fact]
    public void IndicatorRegistrationViewModel_CanBindAndRegister_ColumnAndCriteriaItems()
    {
        var vm = new IndicatorRegistrationViewModel();

        // 1. Test Column item (IndicatorType is null)
        var columnItem = new ScreenerCatalogItem
        {
            CategoryType = ScreenerItemCategoryType.Column,
            GroupName = "Price/Volume",
            ColumnMemberName = "Close",
            ShortName = "Close",
            DisplayName = "Closing Price"
        };

        vm.SelectedIndicator = columnItem;

        Assert.NotNull(vm.LeftSelectedIndicator);
        Assert.Equal("Closing Price", vm.LeftSelectedIndicator.DisplayName);
        Assert.True(vm.RegisterIndicatorCommand.CanExecute(null));

        int initialCount = vm.RegisteredEntries.Count;
        vm.RegisterIndicatorCommand.Execute(null);
        Assert.Equal(initialCount + 1, vm.RegisteredEntries.Count);

        // 2. Test Criteria item (IndicatorType is null)
        var criteriaItem = new ScreenerCatalogItem
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            GroupName = "Pattern Recognition",
            ShortName = "Head and Shoulders",
            DisplayName = "Head & Shoulders Pattern"
        };

        vm.SelectedIndicator = criteriaItem;

        Assert.NotNull(vm.LeftSelectedIndicator);
        Assert.Equal("Head & Shoulders Pattern", vm.LeftSelectedIndicator.DisplayName);
        Assert.True(vm.RegisterIndicatorCommand.CanExecute(null));

        vm.RegisterIndicatorCommand.Execute(null);
        Assert.Equal(initialCount + 2, vm.RegisteredEntries.Count);
    }

    [Fact]
    public void IndicatorRegistrationViewModel_StringColumnSelection_UpdatesOperatorsAndRegistersStringEntry()
    {
        var vm = new IndicatorRegistrationViewModel();

        // Select String Column (e.g. Sector)
        var sectorColumn = new ScreenerCatalogItem
        {
            CategoryType = ScreenerItemCategoryType.Column,
            GroupName = "Basic",
            ColumnMemberName = "Sector",
            ShortName = "Sector",
            DisplayName = "GICS Sector Classification"
        };

        vm.SelectedIndicator = sectorColumn;

        // Assert operator switching to string matching operators
        Assert.True(vm.IsLeftTextColumn);
        Assert.True(vm.IsRightStringMode);
        Assert.Equal(RightHandTargetMode.StringValue, vm.RightTargetMode);
        Assert.Contains(ComparisonOperator.Contains, vm.AvailableComparisonOperators);
        Assert.Contains(ComparisonOperator.DoesNotContain, vm.AvailableComparisonOperators);
        Assert.DoesNotContain(ComparisonOperator.GreaterThan, vm.AvailableComparisonOperators);

        // CanExecute should be false when RightStringValue is empty
        vm.RightStringValue = "";
        Assert.False(vm.RegisterIndicatorCommand.CanExecute(null));

        // CanExecute should be true when RightStringValue has text
        vm.RightStringValue = "Technology";
        Assert.True(vm.RegisterIndicatorCommand.CanExecute(null));

        // Execute registration
        vm.RegisterIndicatorCommand.Execute(null);

        var lastEntry = vm.RegisteredEntries[^1];
        Assert.Equal(RightHandTargetMode.StringValue, lastEntry.TargetMode);
        Assert.Equal(ComparisonOperator.Contains, lastEntry.Operator);
        Assert.Equal("Technology", lastEntry.RightStringValue);
        Assert.Contains("*= \"Technology\"", lastEntry.DisplayName);
        Assert.Contains("Sector", lastEntry.DisplayName);
    }

    [Fact]
    public void IndicatorRegistrationViewModel_AllFiltersGroup_TopPlacement_And_CriteriaFormatting()
    {
        var vm = new IndicatorRegistrationViewModel();

        // 1. Verify "All Filters" is the VERY FIRST item in NavGroups (above Indicators header)
        var firstItem = vm.NavGroups.FirstOrDefault();
        Assert.NotNull(firstItem);
        Assert.False(firstItem.IsHeader);
        Assert.Equal("All Filters", firstItem.Group?.Name);

        // 2. Select "All Filters"
        vm.SelectedGroupItem = firstItem;

        // 3. Verify FilteredIndicators contains catalog items across all categories
        Assert.True(vm.FilteredIndicators.Count > 100);

        // 4. Verify catalog list is strictly sorted alphabetically by DisplayName
        var displayNames = vm.FilteredIndicators.Select(i => i.DisplayName).ToList();
        var sortedDisplayNames = displayNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sortedDisplayNames, displayNames);

        // 5. Test TimeFrameDisplayName, HasPeriod, and PeriodDisplayName
        var indicatorEntry = new ScreenerIndicatorEntry
        {
            TimeFrame = TimeFrame.D1,
            CategoryType = ScreenerItemCategoryType.Indicator,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { { "Period", 14 }, { "UseFilter", false } }
            }
        };
        Assert.Equal("Day", indicatorEntry.TimeFrameDisplayName);
        Assert.False(indicatorEntry.IsCriteria);
        Assert.True(indicatorEntry.HasPeriod);
        Assert.Equal("14", indicatorEntry.PeriodDisplayName);

        var criteriaEntry = new ScreenerIndicatorEntry
        {
            TimeFrame = TimeFrame.D1,
            CategoryType = ScreenerItemCategoryType.Criteria,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                Parameters = new Dictionary<string, object> { { "IsActive", false } }
            }
        };
        Assert.True(criteriaEntry.IsCriteria);
        Assert.False(criteriaEntry.HasPeriod);
        Assert.Equal(string.Empty, criteriaEntry.PeriodDisplayName);
        Assert.Equal("Day", criteriaEntry.TimeFrameDisplayName);
    }

    [Fact]
    public void ThemeColors_ContainsShellSecondaryText_WithDefaultPresets()
    {
        Assert.True(Enum.IsDefined(typeof(StockAnalyzer.Core.Theme.ThemeColorKey), StockAnalyzer.Core.Theme.ThemeColorKey.ShellSecondaryText));
        Assert.NotEqual(default, StockAnalyzer.Core.Theme.ThemeColors.Light.ShellSecondaryText);
        Assert.NotEqual(default, StockAnalyzer.Core.Theme.ThemeColors.Dark.ShellSecondaryText);
    }
}
