using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Tests.ViewModels
{
    public class MockLocalizationService : ILocalizationService
    {
        public string GetString(string key) => key;
    }

    public class MockWatchlistColumnRegistry : IWatchlistColumnRegistry
    {
        public List<WatchlistColumnMetadata> Columns { get; set; } = new();

        public IReadOnlyList<WatchlistColumnMetadata> GetAllColumns()
        {
            return Columns;
        }
    }

    public class FilterSettingsViewModelTests
    {
        private readonly MockLocalizationService _localizationService = new();

        [Fact]
        public void Constructor_NullRegistry_ThrowsArgumentNullException()
        {
            var settings = new FilterSettings();
            Assert.Throws<ArgumentNullException>(() => new FilterSettingsViewModel(settings, _localizationService, null!));
        }

        [Fact]
        public void Constructor_NullColumnsReturned_ThrowsInvalidOperationException()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry { Columns = null! };
            Assert.Throws<InvalidOperationException>(() => new FilterSettingsViewModel(settings, _localizationService, mockRegistry));
        }

        [Fact]
        public void Constructor_EmptyRegistry_ReturnsEmptyAvailableFields()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry { Columns = new List<WatchlistColumnMetadata>() };
            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry);
            Assert.Empty(vm.AvailableFields);
        }

        [Fact]
        public void Constructor_BlacklistedColumns_AreExcluded()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry
            {
                Columns = new List<WatchlistColumnMetadata>
                {
                    new("Col_Select", "IsChecked", "80", 1),
                    new("Col_Symbol", "Symbol", "80", 2),
                    new("Col_ReturnOnEquity", "ReturnOnEquity", "80", 3)
                }
            };

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry);

            // "IsChecked" and "Symbol" should be excluded. "ReturnOnEquity" is allowed.
            // AvailableFields should only have 1 item.
            Assert.Single(vm.AvailableFields);
            Assert.Equal("ReturnOnEquity", vm.AvailableFields[0].PropertyName);

            // AvailableGroups should have 1 group (Ratio)
            Assert.Single(vm.AvailableGroups);
            Assert.Equal("ColumnChooser_Category_Profitability", vm.AvailableGroups[0].GroupName);
            Assert.Single(vm.AvailableGroups[0].Items);
            Assert.Equal("ReturnOnEquity", vm.AvailableGroups[0].Items[0].PropertyName);
        }

        [Fact]
        public void Constructor_DuplicateColumns_FirstWins()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry
            {
                Columns = new List<WatchlistColumnMetadata>
                {
                    new("Col_ReturnOnEquity1", "ReturnOnEquity", "80", 1),
                    new("Col_ReturnOnEquity2", "ReturnOnEquity", "80", 2)
                }
            };

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry);

            // Should have only 1 "ReturnOnEquity" field (the first one)
            Assert.Single(vm.AvailableFields);
            Assert.Equal("ROE", vm.AvailableFields[0].DisplayName);
        }

        [Fact]
        public void Constructor_SortingAndFlattening_CorrectOrderAndHeaders()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry
            {
                Columns = new List<WatchlistColumnMetadata>
                {
                    // Basic category (Sector, Priority 4)
                    new("Col_Sector", "Sector", "80", 4),
                    // Ratio category (ReturnOnEquity, Priority 13)
                    new("Col_ReturnOnEquity", "ReturnOnEquity", "80", 13),
                    // Financial category (Ebitda, Priority 20)
                    new("Col_Ebitda", "Ebitda", "80", 20),
                    // Basic category (Industry, Priority 5)
                    new("Col_Industry", "Industry", "80", 5)
                }
            };

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry);

            // Expected order of categories: Basic -> Ratio -> Financial
            // Under Basic: Sector (Priority 4) -> Industry (Priority 5)
            // Under Ratio: ReturnOnEquity
            // Under Financial: Ebitda

            Assert.Equal(4, vm.AvailableFields.Count);
            Assert.Equal(3, vm.AvailableGroups.Count);

            Assert.Equal("ColumnChooser_Category_Basic", vm.AvailableGroups[0].GroupName);
            Assert.Equal(2, vm.AvailableGroups[0].Items.Count);
            Assert.Equal("Industry", vm.AvailableGroups[0].Items[0].PropertyName);
            Assert.Equal("Sector", vm.AvailableGroups[0].Items[1].PropertyName);

            Assert.Equal("ColumnChooser_Category_Profitability", vm.AvailableGroups[1].GroupName);
            Assert.Single(vm.AvailableGroups[1].Items);
            Assert.Equal("ReturnOnEquity", vm.AvailableGroups[1].Items[0].PropertyName);

            Assert.Equal("ColumnChooser_Category_FinancialHealth", vm.AvailableGroups[2].GroupName);
            Assert.Single(vm.AvailableGroups[2].Items);
            Assert.Equal("Ebitda", vm.AvailableGroups[2].Items[0].PropertyName);
        }

        [Fact]
        public void BuildAvailableFields_PerformanceBenchmark_Under50ms()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry();

            // Populate 200 items with alternating categories
            var categories = new[] { "ReturnOnEquity", "CurrentRatio", "Ebitda", "FreeCashflow", "TrailingPE", "ForwardPE", "Sector", "Industry" };
            for (int i = 0; i < 200; i++)
            {
                var name = $"Field_{i}";
                // Alternate header keys to map to different categories in InfoMap
                var memberName = categories[i % categories.Length] + $"_{i}";
                mockRegistry.Columns.Add(new WatchlistColumnMetadata($"Col_{memberName}", memberName, "80", i));
            }

            var stopwatch = Stopwatch.StartNew();
            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Execution took {stopwatch.ElapsedMilliseconds}ms, which exceeds 50ms limit.");
        }
    }
}
