using System.Reflection;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Tests.Services
{
    public class WatchlistFilterEngineTests
    {
        [Fact]
        public void ContainsTag_ZeroAllocationHelper_EvaluatesCommaSeparatedTagsCorrectly()
        {
            var containsTagMethod = typeof(WatchlistFilterEngine).GetMethod("ContainsTag", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(containsTagMethod);

            bool Evaluate(string tags, string target) => (bool)containsTagMethod.Invoke(null, new object[] { tags, target })!;

            Assert.True(Evaluate("Tech, Semiconductor, Core", "Semiconductor"));
            Assert.True(Evaluate("Tech, Semiconductor, Core", "tech"));
            Assert.True(Evaluate("  Tech ,  Semiconductor ", "Semiconductor"));
            Assert.False(Evaluate("Tech, Semiconductor", "Hardware"));
            Assert.False(Evaluate("", "Semiconductor"));
        }

        [Fact]
        public void EvaluateRule_ROA_AliasAndNumericFiltering_WorksCorrectly()
        {
            var item = new WatchlistItemViewModel("AAPL", "Apple", "Tech", "Hardware", 150m, 158m, 149m, 155m, 1000, 0)
            {
                ReturnOnAssets = 12.5m // 12.5% ROA
            };

            var filterEngine = new WatchlistFilterEngine();

            // Test using MemberName "ReturnOnAssets"
            var rule1 = new FilterRule { Field = "ReturnOnAssets", Operator = ">", Value = "8" };
            Assert.True(filterEngine.EvaluateRule(item, rule1));

            // Test using Alias "ROA"
            var rule2 = new FilterRule { Field = "ROA", Operator = ">", Value = "8" };
            Assert.True(filterEngine.EvaluateRule(item, rule2));

            // Test > 0 condition
            var rule3 = new FilterRule { Field = "ROA", Operator = ">", Value = "0" };
            Assert.True(filterEngine.EvaluateRule(item, rule3));

            // Test condition higher than actual ROA (12.5 > 15 -> False)
            var rule4 = new FilterRule { Field = "ROA", Operator = ">", Value = "15" };
            Assert.False(filterEngine.EvaluateRule(item, rule4));
        }
    }
}
