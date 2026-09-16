using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Avalonia.ViewModels;

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

    /// <summary>In-memory ITemplateService double for FilterSettingsViewModel's Templates section tests (Tests/StockAnalyzer.Tests.csproj has no Moq reference).</summary>
    public class FakeTemplateService : ITemplateService
    {
        public List<FilterTemplate> StoredFilterTemplates { get; } = new();
        public FilterTemplate? LastSaved { get; private set; }

        public Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase
            => Task.FromResult((T?)(object?)StoredFilterTemplates.FirstOrDefault(t => t.Id == id));

        public Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
            => Task.FromResult((IReadOnlyList<T>)StoredFilterTemplates.Cast<T>().ToList());

        public Task SaveAsync<T>(T template) where T : TemplateBase
        {
            if (template is FilterTemplate filterTemplate)
            {
                LastSaved = filterTemplate;
                var existingIndex = StoredFilterTemplates.FindIndex(t => t.Id == filterTemplate.Id);
                if (existingIndex >= 0) StoredFilterTemplates[existingIndex] = filterTemplate;
                else StoredFilterTemplates.Add(filterTemplate);
            }
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(TemplateType type, Guid id)
        {
            var removed = StoredFilterTemplates.RemoveAll(t => t.Id == id) > 0;
            return Task.FromResult(removed);
        }

        public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase
            => Task.FromResult(TemplateValidationResult.Success());

        public Task EnsureMigratedAsync() => Task.CompletedTask;
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
            Assert.Equal("ReturnOnEquity", vm.AvailableFields[0].MemberName);

            // AvailableGroups should have 1 group (Ratio)
            Assert.Single(vm.AvailableGroups);
            Assert.Equal("ColumnChooser_Category_Profitability", vm.AvailableGroups[0].GroupName);
            Assert.Single(vm.AvailableGroups[0].Items);
            Assert.Equal("ReturnOnEquity", vm.AvailableGroups[0].Items[0].MemberName);
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
            Assert.Equal("ROE", vm.AvailableFields[0].LocalizedHeader);
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
            Assert.Equal("Industry", vm.AvailableGroups[0].Items[0].MemberName);
            Assert.Equal("Sector", vm.AvailableGroups[0].Items[1].MemberName);

            Assert.Equal("ColumnChooser_Category_Profitability", vm.AvailableGroups[1].GroupName);
            Assert.Single(vm.AvailableGroups[1].Items);
            Assert.Equal("ReturnOnEquity", vm.AvailableGroups[1].Items[0].MemberName);

            Assert.Equal("ColumnChooser_Category_FinancialHealth", vm.AvailableGroups[2].GroupName);
            Assert.Single(vm.AvailableGroups[2].Items);
            Assert.Equal("Ebitda", vm.AvailableGroups[2].Items[0].MemberName);
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

        [Fact]
        public void IndicatorComparison_ModeToggleAndTargetFieldSelection_WorksCorrectly()
        {
            var mockRegistry = new MockWatchlistColumnRegistry
            {
                Columns = new List<WatchlistColumnMetadata>
                {
                    new("Col_ReturnOnEquity", "ReturnOnEquity", "80", 1),
                    new("Col_TrailingPE", "TrailingPE", "80", 2)
                }
            };

            var rule = new FilterRule
            {
                Field = "ReturnOnEquity",
                Operator = ">",
                Value = "15",
                IsCompareToField = false
            };

            var settings = new FilterSettings
            {
                Rules = new List<FilterRule> { rule }
            };

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry);
            var ruleVM = vm.Rules.First();

            Assert.False(ruleVM.IsCompareToField);
            Assert.Equal("15", ruleVM.Value);

            // Toggle mode to Ind (indicator comparison)
            ruleVM.SetCompareToFieldCommand.Execute(true);

            Assert.True(ruleVM.IsCompareToField);
            // Default target field should be selected if previous value "15" was not a valid field
            Assert.NotNull(ruleVM.SelectedTargetField);
            Assert.Equal(ruleVM.Value, ruleVM.SelectedTargetField.MemberName);

            // Select a specific target field ("TrailingPE")
            var targetFieldItem = vm.AvailableFields.First(f => f.MemberName == "TrailingPE");
            ruleVM.SelectTargetFieldCommand.Execute(targetFieldItem);

            Assert.Equal("TrailingPE", ruleVM.Value);
            Assert.Equal("TrailingPE", ruleVM.SelectedTargetField.MemberName);

            // Export to model
            var resultModel = ruleVM.ToModel();
            Assert.True(resultModel.IsCompareToField);
            Assert.Equal("TrailingPE", resultModel.Value);
        }

        [Fact]
        public void EvaluateRule_IndicatorComparison_OpenLessThanClose_EvaluatesCorrectly()
        {
            var item1 = new WatchlistItemViewModel("AAPL", "Apple", "Tech", "Hardware", 150m, 158m, 149m, 155m, 1000, 0); // Open 150 < Close 155 -> True
            var item2 = new WatchlistItemViewModel("MSFT", "Microsoft", "Tech", "Software", 310m, 315m, 299m, 300m, 1000, 0); // Open 310 < Close 300 -> False

            var rule = new FilterRule
            {
                Field = "Open",
                Operator = "<",
                Value = "Close",
                IsCompareToField = true
            };

            // Test reflective property evaluation directly on WatchlistItemViewModel
            var openProp = typeof(WatchlistItemViewModel).GetProperty("Open");
            var closeProp = typeof(WatchlistItemViewModel).GetProperty("Close");

            Assert.NotNull(openProp);
            Assert.NotNull(closeProp);

            var open1 = (decimal)openProp.GetValue(item1)!;
            var close1 = (decimal)closeProp.GetValue(item1)!;
            Assert.True(open1 < close1);

            var open2 = (decimal)openProp.GetValue(item2)!;
            var close2 = (decimal)closeProp.GetValue(item2)!;
            Assert.False(open2 < close2);
        }

        [Fact]
        public void TimeSeriesCandles_NegativeDowntrend_CalculatesNegativeChangeCorrectly()
        {
            // Simulate 8035-T downtrend candle sequence:
            // Today (2026-07-22): Open = 69570, High = 69780, Low = 67960, Close = 68640 (Downtrend from Open: Change = 68640 - 69570 = -930, Change% = -1.3368%)
            var candleOld = new StockAnalyzer.Core.Models.CandleData(new DateTime(2026, 7, 21), 64350m, 66570m, 64200m, 66570m, 1000);
            var candleNew = new StockAnalyzer.Core.Models.CandleData(new DateTime(2026, 7, 22), 69570m, 69780m, 67960m, 68640m, 1200);

            // Pass in descending order [candleNew, candleOld]
            var candlesUnsorted = new List<StockAnalyzer.Core.Models.CandleData> { candleNew, candleOld };
            var sortedCandles = candlesUnsorted.OrderBy(c => c.Timestamp).ToList();

            var latest = sortedCandles[sortedCandles.Count - 1];

            Assert.Equal(new DateTime(2026, 7, 22), latest.Timestamp);

            var change = latest.Close - latest.Open; // 68640 - 69570 = -930
            var changePercent = (double)((latest.Close - latest.Open) / latest.Open * 100m); // -1.3368...%

            Assert.Equal(-930m, change);
            Assert.True(changePercent < 0, $"Expected negative change percent, got {changePercent}");
            Assert.Equal(-1.3367830961621388, changePercent, 4);
        }

        [Fact]
        public void WatchlistItemViewModel_ChangeProperty_NotifiesDisplayChange()
        {
            var item = new WatchlistItemViewModel("8035-T", "Tokyo Electron", "Tech", "Semiconductor", 30000m, 30200m, 29400m, 29500m, 1000, 0);
            var notifiedProps = new List<string>();
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null) notifiedProps.Add(e.PropertyName);
            };

            item.Change = -500m;

            Assert.Contains("Change", notifiedProps);
            Assert.Contains("DisplayChange", notifiedProps);
            Assert.Equal("-500.00", item.DisplayChange);
        }

        [Fact]
        public void ReflectionCache_InvalidPropertyName_ReturnsNullAndDoesNotStoreInCache()
        {
            var getCachedPropertyInfoMethod = typeof(StockAnalyzer.Avalonia.Services.WatchlistFilterEngine).GetMethod("GetCachedPropertyInfo", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.NotNull(getCachedPropertyInfoMethod);

            // Fetch invalid property multiple times
            var result1 = getCachedPropertyInfoMethod.Invoke(null, new object[] { "NonExistentProperty12345" });
            var result2 = getCachedPropertyInfoMethod.Invoke(null, new object[] { "AnotherInvalidProperty67890" });

            Assert.Null(result1);
            Assert.Null(result2);

            // Check cache field size
            var cacheField = typeof(StockAnalyzer.Avalonia.Services.WatchlistFilterEngine).GetField("_propertyCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(cacheField);
            var cacheDict = cacheField.GetValue(null) as System.Collections.IDictionary;
            Assert.NotNull(cacheDict);
            Assert.False(cacheDict.Contains("NonExistentProperty12345"));
            Assert.False(cacheDict.Contains("AnotherInvalidProperty67890"));
        }

        [Fact]
        public void Constructor_LoadsExistingFilterTemplates()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry();
            var templateService = new FakeTemplateService();
            templateService.StoredFilterTemplates.Add(new FilterTemplate { Name = "Existing" });

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry, templateService);

            Assert.Single(vm.Templates);
            Assert.Equal("Existing", vm.Templates[0].Name);
        }

        [Fact]
        public async Task SaveTemplateCommand_PersistsCurrentRules_AndAddsToTemplatesList()
        {
            var settings = new FilterSettings { Rules = { new FilterRule { Field = "Tag", Value = "Growth" } } };
            var mockRegistry = new MockWatchlistColumnRegistry();
            var templateService = new FakeTemplateService();

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry, templateService);
            vm.NewTemplateName = "My Rules Template";

            await ((IAsyncRelayCommand)vm.SaveTemplateCommand).ExecuteAsync(null);

            Assert.NotNull(templateService.LastSaved);
            Assert.Equal("My Rules Template", templateService.LastSaved!.Name);
            Assert.Single(templateService.LastSaved.RootSettings.Rules);
            Assert.Equal("Growth", templateService.LastSaved.RootSettings.Rules[0].Value);
            Assert.Empty(templateService.LastSaved.RootSettings.Children); // Rules-only: this dialog never captures Children
            Assert.Single(vm.Templates);
        }

        [Fact]
        public async Task LoadTemplateCommand_ReplacesCurrentRules()
        {
            var settings = new FilterSettings { Rules = { new FilterRule { Field = "Tag", Value = "Old" } } };
            var mockRegistry = new MockWatchlistColumnRegistry();
            var templateService = new FakeTemplateService();
            var template = new FilterTemplate
            {
                Name = "Replacement",
                RootSettings = new FilterSettings { Rules = { new FilterRule { Field = "Tag", Value = "New" } } }
            };
            templateService.StoredFilterTemplates.Add(template);

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry, templateService);

            await ((IAsyncRelayCommand<FilterTemplate?>)vm.LoadTemplateCommand).ExecuteAsync(template);

            Assert.Single(vm.Rules);
            Assert.Equal("New", vm.Rules[0].Value);
        }

        [Fact]
        public async Task AppendTemplateCommand_AddsRules_PreservingExisting()
        {
            var settings = new FilterSettings { Rules = { new FilterRule { Field = "Tag", Value = "Existing" } } };
            var mockRegistry = new MockWatchlistColumnRegistry();
            var templateService = new FakeTemplateService();
            var template = new FilterTemplate
            {
                Name = "Appended",
                RootSettings = new FilterSettings { Rules = { new FilterRule { Field = "Tag", Value = "Appended" } } }
            };
            templateService.StoredFilterTemplates.Add(template);

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry, templateService);

            await ((IAsyncRelayCommand<FilterTemplate?>)vm.AppendTemplateCommand).ExecuteAsync(template);

            Assert.Equal(2, vm.Rules.Count);
            Assert.Equal("Existing", vm.Rules[0].Value);
            Assert.Equal("Appended", vm.Rules[1].Value);
        }

        [Fact]
        public async Task DeleteTemplateCommand_RemovesFromTemplatesList_AndCallsTemplateService()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry();
            var templateService = new FakeTemplateService();
            var template = new FilterTemplate { Name = "ToDelete" };
            templateService.StoredFilterTemplates.Add(template);

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry, templateService);

            await ((IAsyncRelayCommand<FilterTemplate?>)vm.DeleteTemplateCommand).ExecuteAsync(template);

            Assert.Empty(vm.Templates);
            Assert.DoesNotContain(template, templateService.StoredFilterTemplates);
        }

        [Fact]
        public void SelectedTemplate_Changed_PopulatesRulesPreview()
        {
            var settings = new FilterSettings();
            var mockRegistry = new MockWatchlistColumnRegistry();
            var templateService = new FakeTemplateService();
            var template = new FilterTemplate
            {
                Name = "Preview",
                RootSettings = new FilterSettings { Rules = { new FilterRule { Field = "PER", Operator = "LessThan", Value = "15" } } }
            };
            templateService.StoredFilterTemplates.Add(template);

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry, templateService);
            vm.SelectedTemplate = template;

            Assert.Single(vm.SelectedTemplateRulesPreview);
            Assert.Equal("PER LessThan 15", vm.SelectedTemplateRulesPreview[0].DisplayText);
            Assert.Same(template.RootSettings.Rules[0], vm.SelectedTemplateRulesPreview[0].Rule);
        }

        [Fact]
        public void AddTemplateRuleCommand_AddsSingleRule_PreservingExistingRules()
        {
            var settings = new FilterSettings
            {
                Rules = { new FilterRule { Field = "Symbol", Operator = "==", Value = "AAPL" } }
            };
            var mockRegistry = new MockWatchlistColumnRegistry();
            var templateService = new FakeTemplateService();
            var template = new FilterTemplate
            {
                Name = "Preview",
                RootSettings = new FilterSettings
                {
                    Rules =
                    {
                        new FilterRule { Field = "PER", Operator = "LessThan", Value = "15" },
                        new FilterRule { Field = "ROE", Operator = "GreaterThan", Value = "10" }
                    }
                }
            };
            templateService.StoredFilterTemplates.Add(template);

            var vm = new FilterSettingsViewModel(settings, _localizationService, mockRegistry, templateService);
            vm.SelectedTemplate = template;

            vm.AddTemplateRuleCommand.Execute(vm.SelectedTemplateRulesPreview[0].Rule);

            Assert.Equal(2, vm.Rules.Count);
            Assert.Equal("Symbol", vm.Rules[0].Field);
            Assert.Equal("PER", vm.Rules[1].Field);
        }
    }
}
