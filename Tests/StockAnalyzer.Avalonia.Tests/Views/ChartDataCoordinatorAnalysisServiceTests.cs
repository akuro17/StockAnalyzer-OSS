using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// <see cref="ChartDataCoordinator"/> gets its analysis service through an optional last constructor parameter. Without it the coordinator builds the same
/// default service as before, so existing callers (ChartBaseControl) behave identically; with it a test can observe which service the coordinator uses.
/// </summary>
public class ChartDataCoordinatorAnalysisServiceTests
{
    private sealed class FixedAtrAnalysisService : IAnalysisPipelineService
    {
        private readonly decimal _atr;
        public int AtrCalls { get; private set; }

        public FixedAtrAnalysisService(decimal atr) => _atr = atr;

        public decimal CalculateAtr(IReadOnlyList<CoreCandleData> candles, int period)
        {
            AtrCalls++;
            return _atr;
        }

        public Dictionary<string, IIndicatorResult> CalculateIndicators(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings,
            IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles,
            IEnumerable<CoreIndicatorSettings> settings, IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)
            => throw new NotSupportedException();

        public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true,
            bool isLogScaleVolume = false, int dataCount = 0) => throw new NotSupportedException();
    }

    private static CoreCandleData[] Candles() => Enumerable.Range(0, 40)
        .Select(i => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), 100m + i, 103m + i, 98m + i, 101m + i * 1.1m, 1000 + i))
        .ToArray();

    private static ChartViewModel AutoAtrRenkoViewModel()
    {
        var vm = new ChartViewModel
        {
            Candles = Candles(),
            RenkoSizingMode = ChartSizingMode.AutoAtr,
            RenkoAtrMultiplier = 2m,
            RenkoAtrPeriod = 14,
        };
        return vm;
    }

    [Fact]
    public void SyncEffectiveValues_UsesTheInjectedAnalysisService()
    {
        var vm = AutoAtrRenkoViewModel();
        var service = new FixedAtrAnalysisService(1.5m);
        var coordinator = new ChartDataCoordinator(analysisService: service);

        coordinator.SyncEffectiveValues(vm);

        Assert.True(service.AtrCalls >= 1);
        Assert.Equal(3m, vm.EffectiveRenkoBrickSize);   // 1.5 ATR x multiplier 2
    }

    [Fact]
    public void SyncEffectiveValues_WithoutAnInjectedService_UsesTheDefaultAnalysisPipeline()
    {
        var vm = AutoAtrRenkoViewModel();
        decimal expectedAtr = new AnalysisPipelineService().CalculateAtr(vm.Candles!, vm.RenkoAtrPeriod);
        Assert.True(expectedAtr > 0m);
        var coordinator = new ChartDataCoordinator();

        coordinator.SyncEffectiveValues(vm);

        Assert.Equal(expectedAtr * vm.RenkoAtrMultiplier, vm.EffectiveRenkoBrickSize);
    }
}
