using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// Verifies that ScreenerTemplateEvaluator's entry-combination convention (union on the preceding
/// entry's LogicalOperator.Or, intersect otherwise) matches ScreenerViewModel.ScanAsync's existing,
/// already-shipped convention - this is a new evaluator built to the same rule, not a modification of
/// either existing implementation. IScreeningCondition/IScreenerService are substituted here (ordinary
/// service-layer interfaces, not CandleData/IIndicator/IndicatorType, so this is not the prohibited
/// domain-model mocking).
/// </summary>
public class ScreenerTemplateEvaluatorTests
{
    private class FakeScreenerService : IScreenerService
    {
        private readonly Dictionary<IScreeningCondition, List<string>> _resultsByCondition;

        public FakeScreenerService(Dictionary<IScreeningCondition, List<string>> resultsByCondition)
        {
            _resultsByCondition = resultsByCondition;
        }

        public Task<List<string>> ScreenAsync(ScreeningCriteria criteria, IProgress<int> progress, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<List<string>> ScreenAsync(List<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, IProgress<int> progress, CancellationToken ct) =>
            Task.FromResult(_resultsByCondition.TryGetValue(condition, out var result) ? result : new List<string>());
    }

    [Fact]
    public async Task EvaluateAsync_TwoEntriesWithAndOperator_IntersectsResults()
    {
        var entryA = new ScreenerIndicatorEntry { Label = "A", LogicalOperator = LogicalOperator.And, IsEnabled = true };
        var entryB = new ScreenerIndicatorEntry { Label = "B", IsEnabled = true };
        var template = new ScreenerIndicatorTemplate { Name = "Test" };
        template.SetEntries(new[] { entryA, entryB });

        var fakeService = new FakeScreenerService(new Dictionary<IScreeningCondition, List<string>>
        {
            [entryA] = new List<string> { "AAPL", "MSFT", "GOOGL" },
            [entryB] = new List<string> { "MSFT", "GOOGL", "TSLA" },
        });
        var evaluator = new ScreenerTemplateEvaluator(fakeService);

        var result = await evaluator.EvaluateAsync(
            new[] { "AAPL", "MSFT", "GOOGL", "TSLA" }, template, TimeFrame.D1, null, CancellationToken.None);

        Assert.Equal(new HashSet<string> { "MSFT", "GOOGL" }, result);
    }

    [Fact]
    public async Task EvaluateAsync_TwoEntriesWithOrOperator_UnionsResults()
    {
        var entryA = new ScreenerIndicatorEntry { Label = "A", LogicalOperator = LogicalOperator.Or, IsEnabled = true };
        var entryB = new ScreenerIndicatorEntry { Label = "B", IsEnabled = true };
        var template = new ScreenerIndicatorTemplate { Name = "Test" };
        template.SetEntries(new[] { entryA, entryB });

        var fakeService = new FakeScreenerService(new Dictionary<IScreeningCondition, List<string>>
        {
            [entryA] = new List<string> { "AAPL" },
            [entryB] = new List<string> { "MSFT" },
        });
        var evaluator = new ScreenerTemplateEvaluator(fakeService);

        var result = await evaluator.EvaluateAsync(
            new[] { "AAPL", "MSFT" }, template, TimeFrame.D1, null, CancellationToken.None);

        Assert.Equal(new HashSet<string> { "AAPL", "MSFT" }, result);
    }

    [Fact]
    public async Task EvaluateAsync_DisabledEntries_AreExcludedFromEvaluation()
    {
        var entryA = new ScreenerIndicatorEntry { Label = "A", IsEnabled = false };
        var entryB = new ScreenerIndicatorEntry { Label = "B", IsEnabled = true };
        var template = new ScreenerIndicatorTemplate { Name = "Test" };
        template.SetEntries(new[] { entryA, entryB });

        var fakeService = new FakeScreenerService(new Dictionary<IScreeningCondition, List<string>>
        {
            [entryB] = new List<string> { "MSFT" },
        });
        var evaluator = new ScreenerTemplateEvaluator(fakeService);

        var result = await evaluator.EvaluateAsync(
            new[] { "MSFT" }, template, TimeFrame.D1, null, CancellationToken.None);

        Assert.Equal(new HashSet<string> { "MSFT" }, result);
    }

    [Fact]
    public async Task EvaluateAsync_NoEnabledEntries_ReturnsEmpty()
    {
        var entryA = new ScreenerIndicatorEntry { Label = "A", IsEnabled = false };
        var template = new ScreenerIndicatorTemplate { Name = "Test" };
        template.SetEntries(new[] { entryA });

        var evaluator = new ScreenerTemplateEvaluator(new FakeScreenerService(new()));

        var result = await evaluator.EvaluateAsync(
            new[] { "MSFT" }, template, TimeFrame.D1, null, CancellationToken.None);

        Assert.Empty(result);
    }
}
