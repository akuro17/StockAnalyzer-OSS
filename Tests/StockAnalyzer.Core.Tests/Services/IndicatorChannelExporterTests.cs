using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Exercises <see cref="IndicatorChannelExporter"/> (Task2/Task7 of the
/// TrainingWizard_IndicatorChannelExporter feature): the empty-map fast path for Price-only specs,
/// the non-Daily-timeframe / unregistered-type / non-default-Params guard clauses, and that a
/// registered default-Params Indicator channel is exported to a parquet file whose <c>date</c> +
/// <c>channel_{index}</c> columns match a direct <see cref="ICoreIndicator.Calculate"/> call on the
/// same candles (the exporter and <see cref="PredictionService.BuildComposedTensor"/> must agree,
/// since both read <see cref="IIndicatorResult.MainValues"/> from the same formula source).
/// </summary>
public sealed class IndicatorChannelExporterTests
{
    // Close = 100+i for i=0..N-1 makes CoreSmaIndicator's default Period=20 result hand-computable
    // (mean of 20 consecutive integers) without depending on the indicator's own implementation.
    private static List<CandleData> BuildCandles(int count)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000));
        }
        return candles;
    }

    private static FeatureSpec IndicatorOnlySpec(IndicatorType type = IndicatorType.SMA, IReadOnlyDictionary<string, string>? @params = null)
        => new()
        {
            Channels = new[]
            {
                new FeatureChannel
                {
                    Kind = FeatureChannelKind.Indicator,
                    Indicator = type,
                    Params = @params ?? new Dictionary<string, string>(),
                },
            },
        };

    // No test in this class exercising the empty-map / non-Daily guard clauses reaches
    // GetTickersDataAsync, so throwing from every member catches an accidental change in call order.
    private sealed class NeverCalledMarketDataProvider : IMarketDataProvider
    {
        private static NotSupportedException NotExpected([System.Runtime.CompilerServices.CallerMemberName] string member = "")
            => new($"{member} should not be called by this test.");

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => throw NotExpected();
        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => throw NotExpected();
        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => throw NotExpected();
        public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => throw NotExpected();
        public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => throw NotExpected();
        public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => throw NotExpected();
        public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => throw NotExpected();
        public Task AddTickerAsync(string symbol) => throw NotExpected();
        public Task AddTickersAsync(IEnumerable<string> symbols) => throw NotExpected();
        public Task RemoveTickerAsync(string symbol) => throw NotExpected();
        public Task RemoveTickersAsync(IEnumerable<string> symbols) => throw NotExpected();
        public void InvalidateMetadataCache(string ticker) => throw NotExpected();
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => throw NotExpected();
        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => throw NotExpected();
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<CandleData>> _bySymbol;
        public FakeMarketDataProvider(IReadOnlyDictionary<string, IReadOnlyList<CandleData>> bySymbol) => _bySymbol = bySymbol;

        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame)
            => _bySymbol.TryGetValue(symbol, out var candles)
                ? Task.FromResult(candles)
                : throw new KeyNotFoundException($"FakeMarketDataProvider: no fixture candles configured for '{symbol}'.");

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
        public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => throw new NotSupportedException();
        public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => throw new NotSupportedException();
        public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => throw new NotSupportedException();
        public Task AddTickerAsync(string symbol) => throw new NotSupportedException();
        public Task AddTickersAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
        public Task RemoveTickerAsync(string symbol) => throw new NotSupportedException();
        public Task RemoveTickersAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
        public void InvalidateMetadataCache(string ticker) => throw new NotSupportedException();
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => throw new NotSupportedException();
        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => throw new NotSupportedException();
    }

    // Deterministic stand-in for "not registered": the real reflection-discovered
    // IndicatorFactory.Default has no IndicatorType value guaranteed to stay unregistered across
    // builds (same rationale as PredictionServiceTests.NeverRegisteredIndicatorFactory).
    private sealed class AlwaysUnregisteredIndicatorFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => null;
        public bool IsRegistered(IndicatorType type) => false;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => Enumerable.Empty<IndicatorType>();
    }

    [Fact]
    public async Task ExportAsync_PriceOnlySpec_ReturnsEmptyMapWithoutTouchingMarketDataProvider()
    {
        var spec = new FeatureSpec
        {
            Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close } },
        };
        var exporter = new IndicatorChannelExporter(new NeverCalledMarketDataProvider(), IndicatorFactory.Default);

        var result = await exporter.ExportAsync(spec, new[] { "TESTSYM" }, TrainingTimeframe.Daily, "run1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ExportAsync_NonDailyTimeframeWithIndicatorChannel_ThrowsWithoutTouchingMarketDataProvider()
    {
        var exporter = new IndicatorChannelExporter(new NeverCalledMarketDataProvider(), IndicatorFactory.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exporter.ExportAsync(IndicatorOnlySpec(), new[] { "TESTSYM" }, TrainingTimeframe.Weekly, "run1"));
    }

    [Fact]
    public async Task ExportAsync_UnregisteredIndicatorType_ThrowsWithoutTouchingMarketDataProvider()
    {
        var exporter = new IndicatorChannelExporter(new NeverCalledMarketDataProvider(), new AlwaysUnregisteredIndicatorFactory());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exporter.ExportAsync(IndicatorOnlySpec(), new[] { "TESTSYM" }, TrainingTimeframe.Daily, "run1"));
    }

    [Fact]
    public async Task ExportAsync_NonDefaultParams_ReflectsCustomPeriodInExportedValues()
    {
        const string symbol = "TESTSYM";
        var candles = BuildCandles(30);
        var provider = new FakeMarketDataProvider(new Dictionary<string, IReadOnlyList<CandleData>> { [symbol] = candles });
        // CoreSmaParameter's "Period" property name comes from FeatureChannelConverterTests'
        // established convention (matched by property name, case-insensitive).
        var spec = IndicatorOnlySpec(@params: new Dictionary<string, string> { ["Period"] = "10" });
        var exporter = new IndicatorChannelExporter(provider, IndicatorFactory.Default);
        var runId = "run_" + Guid.NewGuid().ToString("N");

        string? exportPath = null;
        try
        {
            var result = await exporter.ExportAsync(spec, new[] { symbol }, TrainingTimeframe.Daily, runId);
            exportPath = result[symbol];

            // Close = 100+i, SMA(10) at bar i>=9 is the mean of 10 consecutive integers ending at
            // 100+i, i.e. 100+i-4.5 -- distinct from SMA(20)'s 100+i-9.5 used elsewhere in this
            // file, proving the custom Period=10 (not the registry default Period=20) was applied.
            var (dates, values) = ReadDateAndChannel(exportPath, channelIndex: 0);
            Assert.Equal(candles.Count, dates.Count);
            for (int i = 0; i < 9; i++)
            {
                Assert.Null(values[i]);
            }
            for (int i = 9; i < candles.Count; i++)
            {
                Assert.NotNull(values[i]);
                Assert.Equal(100 + i - 4.5, values[i]!.Value, precision: 6);
            }
        }
        finally
        {
            if (exportPath is not null) TryDelete(exportPath);
        }
    }

    [Fact]
    public async Task ExportAsync_RegisteredIndicatorDefaultParams_WritesParquetMatchingDirectCalculation()
    {
        const string symbol = "TESTSYM";
        var candles = BuildCandles(30);
        var provider = new FakeMarketDataProvider(new Dictionary<string, IReadOnlyList<CandleData>> { [symbol] = candles });
        var exporter = new IndicatorChannelExporter(provider, IndicatorFactory.Default);
        var runId = "run_" + Guid.NewGuid().ToString("N");

        string? exportPath = null;
        try
        {
            var result = await exporter.ExportAsync(IndicatorOnlySpec(), new[] { symbol }, TrainingTimeframe.Daily, runId);

            Assert.True(result.TryGetValue(symbol, out exportPath));
            Assert.True(File.Exists(exportPath));

            var coreCandles = candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
            var expected = IndicatorFactory.Default.Create(IndicatorType.SMA)!.Calculate(coreCandles).MainValues;

            var (dates, values) = ReadDateAndChannel(exportPath!, channelIndex: 0);

            Assert.Equal(candles.Count, dates.Count);
            for (int i = 0; i < candles.Count; i++)
            {
                Assert.Equal(candles[i].Timestamp.Date, dates[i].Date);
                if (expected[i].HasValue)
                {
                    Assert.NotNull(values[i]);
                    Assert.Equal((double)expected[i]!.Value, values[i]!.Value, precision: 6);
                }
                else
                {
                    Assert.Null(values[i]);
                }
            }
        }
        finally
        {
            if (exportPath is not null) TryDelete(exportPath);
        }
    }

    [Fact]
    public async Task ExportAsync_MixedPriceAndIndicatorChannels_NamesColumnByOriginalChannelIndex()
    {
        const string symbol = "TESTSYM";
        var candles = BuildCandles(25);
        var provider = new FakeMarketDataProvider(new Dictionary<string, IReadOnlyList<CandleData>> { [symbol] = candles });
        var exporter = new IndicatorChannelExporter(provider, IndicatorFactory.Default);
        var runId = "run_" + Guid.NewGuid().ToString("N");
        // Channel [0] = Price (not exported), Channel [1] = Indicator: the exporter must name the
        // written column "channel_1" (the channel's own index in Channels), not "channel_0" (its
        // index among Indicator-kind channels only) -- dataset.py's _build_indicator_columns reads
        // columns by the FeatureSpec's own channel index.
        var spec = new FeatureSpec
        {
            Channels = new[]
            {
                new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close },
                new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA },
            },
        };

        string? exportPath = null;
        try
        {
            var result = await exporter.ExportAsync(spec, new[] { symbol }, TrainingTimeframe.Daily, runId);
            exportPath = result[symbol];

            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM read_parquet('{exportPath.Replace("\\", "/")}') LIMIT 0";
            using var reader = command.ExecuteReader();
            var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

            Assert.Contains("channel_1", columnNames);
            Assert.DoesNotContain("channel_0", columnNames);
        }
        finally
        {
            if (exportPath is not null) TryDelete(exportPath);
        }
    }

    private static (List<DateTime> Dates, List<double?> Values) ReadDateAndChannel(string parquetPath, int channelIndex)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT date, channel_{channelIndex} FROM read_parquet('{parquetPath.Replace("\\", "/")}') ORDER BY date ASC";
        using var reader = command.ExecuteReader();

        var dates = new List<DateTime>();
        var values = new List<double?>();
        while (reader.Read())
        {
            dates.Add(reader.GetDateTime(0));
            values.Add(reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1)));
        }
        return (dates, values);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
