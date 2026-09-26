using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

public class BacktestRunPreparationServiceTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class RecordingDataService : IDataService
    {
        private readonly IReadOnlyDictionary<TimeFrame, IReadOnlyList<CandleData>> _bars;
        public List<(string Symbol, TimeFrame Frame)> Calls { get; } = new();

        public RecordingDataService(IReadOnlyDictionary<TimeFrame, IReadOnlyList<CandleData>> bars) => _bars = bars;

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            Calls.Add((symbol, timeFrame));
            return Task.FromResult(_bars.TryGetValue(timeFrame, out IReadOnlyList<CandleData>? value)
                ? value
                : (IReadOnlyList<CandleData>)Array.Empty<CandleData>());
        }
    }

    private static CandleData Bar(int day, decimal price = 100m) =>
        new(Start.AddDays(day), price, price, price, price, 1000);

    private static BacktestConfiguration Configuration(ExecutionModel executionModel = ExecutionModel.Legacy) => new()
    {
        ExecutionModel = executionModel,
        InitialCapital = 1_000_000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static BacktestRunSnapshot Snapshot(
        ImmutableArray<BacktestConditionEntry> conditions = default,
        ImmutableArray<StrategyIndicatorRequest> selected = default,
        DateTime? end = null) => new(
            "TEST",
            TimeFrame.D1,
            Start,
            end ?? Start.AddDays(10),
            Configuration(),
            conditions.IsDefault ? ImmutableArray<BacktestConditionEntry>.Empty : conditions,
            null,
            selected.IsDefault ? ImmutableArray<StrategyIndicatorRequest>.Empty : selected,
            BacktestReportDefaults.BuiltIn,
            42,
            2000);

    [Fact]
    public async Task RawMainSeries_IsValidatedBeforeEndDateBinarySearch()
    {
        var data = new RecordingDataService(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.D1] = new[] { Bar(0), Bar(2), Bar(1) },
        });
        var service = new BacktestRunPreparationService(data);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(Snapshot(end: Start), CancellationToken.None));
    }

    [Fact]
    public async Task StrictEvidence_FailsClosedBeforeLoadingLegacyBars()
    {
        var data = new RecordingDataService(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>());
        var service = new BacktestRunPreparationService(data);
        BacktestRunSnapshot snapshot = Snapshot() with
        {
            Configuration = Configuration(ExecutionModel.StrictEvidence),
        };

        await Assert.ThrowsAsync<StrictEvidenceAdapterUnavailableException>(() =>
            service.PrepareAsync(snapshot, CancellationToken.None));

        Assert.Empty(data.Calls);
    }

    [Fact]
    public async Task YFinanceApproximate_AcceptsUserDeclaredDailySource()
    {
        var data = new RecordingDataService(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.D1] = new[] { Bar(0), Bar(1) },
        });
        var service = new BacktestRunPreparationService(data);
        BacktestRunSnapshot snapshot = Snapshot() with
        {
            Configuration = Configuration(ExecutionModel.YFinanceApproximate),
        };

        PreparedBacktestRun prepared = await service.PrepareAsync(snapshot, CancellationToken.None);

        Assert.Equal(ExecutionModel.YFinanceApproximate, prepared.Configuration.ExecutionModel);
        Assert.Equal(TimeFrame.D1, Assert.Single(data.Calls).Frame);
        Assert.Null(prepared.Input.AdditionalTimeframeBars);
    }

    [Fact]
    public async Task YFinanceApproximate_RejectsOtherFrameAndConfiguredSourceBeforeLoading()
    {
        var data = new RecordingDataService(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>());
        var service = new BacktestRunPreparationService(data);
        BacktestRunSnapshot snapshot = Snapshot() with
        {
            Configuration = Configuration(ExecutionModel.YFinanceApproximate),
        };

        await Assert.ThrowsAsync<YFinanceApproximateSourceUnavailableException>(() =>
            service.PrepareAsync(snapshot with { Frame = TimeFrame.W1 }, CancellationToken.None));
        var otherSource = new BacktestRunPreparationService(data, Options.Create(new MarketDataSettings
        {
            DailyDataPath = Path.GetTempPath(),
        }));
        await Assert.ThrowsAsync<YFinanceApproximateSourceUnavailableException>(() =>
            otherSource.PrepareAsync(snapshot, CancellationToken.None));
        Assert.Empty(data.Calls);
    }

    [Fact]
    public async Task YFinanceApproximate_RejectsForeignFrameBeforeLoadingIt()
    {
        var data = new RecordingDataService(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.D1] = new[] { Bar(0), Bar(1) },
        });
        var service = new BacktestRunPreparationService(data);
        var selected = ImmutableArray.Create(new StrategyIndicatorRequest("a", IndicatorType.SMA, null, TimeFrame.W1));
        BacktestRunSnapshot snapshot = Snapshot(selected: selected) with
        {
            Configuration = Configuration(ExecutionModel.YFinanceApproximate),
        };

        await Assert.ThrowsAsync<YFinanceApproximateSourceUnavailableException>(() =>
            service.PrepareAsync(snapshot, CancellationToken.None));
        Assert.Equal(TimeFrame.D1, Assert.Single(data.Calls).Frame);
    }

    [Fact]
    public async Task NoOpForeignFrameOverride_IsLoadedExactlyOnce()
    {
        var data = new RecordingDataService(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.D1] = new[] { Bar(0), Bar(1) },
            [TimeFrame.W1] = new[] { Bar(0) },
        });
        var service = new BacktestRunPreparationService(data);
        var selected = ImmutableArray.Create(
            new StrategyIndicatorRequest("a", IndicatorType.SMA, null, TimeFrame.W1),
            new StrategyIndicatorRequest("b", IndicatorType.EMA, null, TimeFrame.W1));

        PreparedBacktestRun prepared = await service.PrepareAsync(Snapshot(selected: selected), CancellationToken.None);

        Assert.IsType<NoOpBacktestStrategy>(prepared.Strategy);
        Assert.Equal(1, data.Calls.Count(call => call.Frame == TimeFrame.W1));
        Assert.True(prepared.Input.AdditionalTimeframeBars!.ContainsKey(TimeFrame.W1));
    }

    [Fact]
    public async Task NumericTargetStaleRight_DoesNotLoadItsForeignFrame()
    {
        var data = new RecordingDataService(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.D1] = new[] { Bar(0), Bar(1) },
            [TimeFrame.W1] = new[] { Bar(0) },
        });
        var service = new BacktestRunPreparationService(data);
        ImmutableArray<BacktestConditionEntry> conditions = BacktestConditionValidator.Snapshot(new[]
        {
            new BacktestConditionEntry
            {
                Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price },
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 100m,
                Right = new BacktestConditionSide
                {
                    IndicatorType = IndicatorType.ZigZag,
                    Frame = TimeFrame.W1,
                    Offset = -1,
                },
            },
        });

        PreparedBacktestRun prepared = await service.PrepareAsync(Snapshot(conditions: conditions), CancellationToken.None);

        Assert.IsType<ConditionBasedBacktestStrategy>(prepared.Strategy);
        Assert.DoesNotContain(data.Calls, call => call.Frame == TimeFrame.W1);
        Assert.Null(prepared.Input.AdditionalTimeframeBars);
    }
}
