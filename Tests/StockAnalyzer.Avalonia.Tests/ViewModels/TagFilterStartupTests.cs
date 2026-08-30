using Xunit;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Avalonia.Services;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Tests.ViewModels
{
    public class TagFilterStartupTests
    {
        [Fact]
        public void WatchlistItemViewModel_IsMetadataLoaded_InitialValueShouldBeFalse()
        {
            var item = new WatchlistItemViewModel("AAPL", "AAPL", "", "", 0, 0, 0, 0, 0, 0);
            Assert.False(item.IsMetadataLoaded);
        }

        [Fact]
        public void TagFilterEngine_EvaluateSettings_WithTag_ShouldMatchCorrectly()
        {
            var engine = new WatchlistFilterEngine();
            var item = new WatchlistItemViewModel("AAPL", "Apple", "Tech", "Consumer Electronics", 150m, 155m, 149m, 153m, 1000, 2.0, 3m);
            item.Tag = "Growth, US";
            item.IsMetadataLoaded = true;

            var filter = new FilterSettings
            {
                Name = "Growth Tag Filter",
                Rules = new List<FilterRule>
                {
                    new FilterRule
                    {
                        Field = "Tag",
                        Operator = "Contains",
                        Value = "Growth"
                    }
                }
            };

            Assert.True(engine.EvaluateSettings(item, filter));
        }
    }
}
