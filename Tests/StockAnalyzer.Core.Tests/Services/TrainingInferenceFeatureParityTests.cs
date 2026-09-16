using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Task3 of the IndicatorChannelResolver_WebAIReviewAdoption plan (see
/// <c>Y:\Temp\sa_implementation_plan_IndicatorChannelResolver_WebAIReviewAdoption.md</c>): proves the
/// design's central claim directly -- "the same FeatureSpec computes the same values whether it goes
/// through the offline <see cref="IndicatorChannelExporter"/> or the online
/// <see cref="PredictionService.BuildComposedTensor"/>" -- rather than only individually testing each
/// service against hand-computed values (as <c>IndicatorChannelExporterTests</c> and
/// <c>PredictionServiceTests</c> already do). Runs the same <see cref="FeatureSpec"/> and candles
/// through both, via the shared <see cref="FeatureChannelConverter.ResolveParameterObject"/> resolution
/// path both now use, and asserts per-bar parity.
/// </summary>
public sealed class TrainingInferenceFeatureParityTests
{
    private static List<CandleData> BuildCandles(int count)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000));
        }
        return candles;
    }

    public static IEnumerable<object[]> IndicatorChannelCases()
    {
        yield return new object[] { IndicatorType.SMA, new Dictionary<string, string>() };
        yield return new object[] { IndicatorType.SMA, new Dictionary<string, string> { ["Period"] = "10" } };
        yield return new object[] { IndicatorType.EMA, new Dictionary<string, string>() };
        yield return new object[] { IndicatorType.RSI, new Dictionary<string, string>() };
    }

    [Theory]
    [MemberData(nameof(IndicatorChannelCases))]
    public async Task ExporterAndPredictionService_SingleIndicatorChannel_ProduceIdenticalValues(
        IndicatorType indicatorType, Dictionary<string, string> @params)
    {
        const string symbol = "TESTSYM";
        var candles = BuildCandles(40);
        var spec = new FeatureSpec
        {
            Channels = new[]
            {
                new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = indicatorType, Params = @params },
            },
        };

        var (_, exportedValues) = await ExportAndReadBackAsync(spec, symbol, candles);

        // PredictionService.BuildComposedTensor throws for any window containing an unwarmed (null)
        // bar, so only the fully-warmed tail is compared: SMA/EMA/RSI never return to null once
        // warmed for this strictly-increasing, gap-free fixture.
        int firstWarm = exportedValues.FindIndex(v => v.HasValue);
        Assert.True(firstWarm >= 0, "Fixture must produce at least one warmed bar to compare.");
        int windowSize = candles.Count - firstWarm;

        var destination = new float[windowSize];
        PredictionService.BuildComposedTensor(candles, startIndex: firstWarm, windowSize: windowSize, spec, IndicatorFactory.Default, destination);

        for (int i = 0; i < windowSize; i++)
        {
            var exported = exportedValues[firstWarm + i];
            Assert.True(exported.HasValue, $"Expected exporter value at bar {firstWarm + i} to be warmed.");
            Assert.Equal(exported!.Value, (double)destination[i], precision: 4);
        }
    }

    [Fact]
    public async Task ExporterAndPredictionService_PriceAndIndicatorMixedChannels_IndicatorChannelValuesMatch()
    {
        const string symbol = "TESTSYM";
        var candles = BuildCandles(40);
        var spec = new FeatureSpec
        {
            Channels = new[]
            {
                new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close },
                new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA },
            },
        };

        var (_, exportedValues) = await ExportAndReadBackAsync(spec, symbol, candles, channelIndex: 1);

        int firstWarm = exportedValues.FindIndex(v => v.HasValue);
        Assert.True(firstWarm >= 0, "Fixture must produce at least one warmed bar to compare.");
        int windowSize = candles.Count - firstWarm;
        var destination = new float[windowSize * spec.Channels.Count];
        PredictionService.BuildComposedTensor(candles, startIndex: firstWarm, windowSize: windowSize, spec, IndicatorFactory.Default, destination);

        for (int i = 0; i < windowSize; i++)
        {
            var exported = exportedValues[firstWarm + i];
            Assert.True(exported.HasValue);
            // Bar-major / channel-minor layout: destination[bar * channelCount + channel].
            var predicted = destination[(i * spec.Channels.Count) + 1];
            Assert.Equal(exported!.Value, (double)predicted, precision: 4);
        }
    }

    private static async Task<(List<DateTime> Dates, List<double?> Values)> ExportAndReadBackAsync(
        FeatureSpec spec, string symbol, List<CandleData> candles, int channelIndex = 0)
    {
        var provider = new FakeMarketDataProvider(new Dictionary<string, IReadOnlyList<CandleData>> { [symbol] = candles });
        var exporter = new IndicatorChannelExporter(provider, IndicatorFactory.Default);
        var runId = "run_" + Guid.NewGuid().ToString("N");

        var result = await exporter.ExportAsync(spec, new[] { symbol }, TrainingTimeframe.Daily, runId);
        var exportPath = result[symbol];
        try
        {
            return ReadDateAndChannel(exportPath, channelIndex);
        }
        finally
        {
            try { File.Delete(exportPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static (List<DateTime> Dates, List<double?> Values) ReadDateAndChannel(string parquetPath, int channelIndex)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        var escapedPath = ParquetMarketDataProvider.EscapeDuckDbPath(parquetPath.Replace("\\", "/"));
        command.CommandText = $"SELECT date, channel_{channelIndex} FROM read_parquet('{escapedPath}') ORDER BY date ASC";
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
}
