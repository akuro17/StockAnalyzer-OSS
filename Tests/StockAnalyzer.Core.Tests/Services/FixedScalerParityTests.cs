using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class FixedScalerParityTests
{
    [Fact]
    public void RawLagsAndFloat32TransformMatchSharedPythonFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!.FullName, "Tests", "FixedScalerParity.json")));
        var root = fixture.RootElement;
        var metadata = new Dictionary<string, string>
        {
            ["feature_mode"] = "ohlcv_minmax", ["window_size"] = "2", ["channels"] = "10",
            ["normalization"] = "fixed_zscore", ["lags"] = "[1]", ["clip_sigma"] = "3.0",
        };
        var scaler = JsonSerializer.Deserialize<FixedScaler>(root.GetProperty("scaler").GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })! with
        { FeatureContractHash = FixedScaler.ComputeContractHash(metadata) };
        scaler.Validate(metadata);
        var candles = root.GetProperty("ohlcv").EnumerateArray().Select((row, index) =>
        {
            var columns = row.EnumerateArray().ToArray();
            return new CandleData(DateTime.UnixEpoch.AddDays(index),
                columns[0].GetDecimal(), columns[1].GetDecimal(), columns[2].GetDecimal(),
                columns[3].GetDecimal(), columns[4].GetInt64());
        }).ToArray();
        var rawWindows = root.GetProperty("raw_windows");
        var scaledWindows = root.GetProperty("scaled_windows");
        for (int sample = 0; sample < rawWindows.GetArrayLength(); sample++)
        {
            var raw = new double[scaler.WindowSize * scaler.Statistics.Length];
            PredictionService.BuildFixedRawTensor(candles, sample + 1, scaler, null,
                IndicatorFactory.Default, raw);
            var expectedRaw = rawWindows[sample].EnumerateArray()
                .SelectMany(row => row.EnumerateArray().Select(value => value.GetDouble())).ToArray();
            Assert.Equal(expectedRaw, raw);
            var output = new float[raw.Length];
            scaler.Transform(raw, output);
            var expected = scaledWindows[sample].EnumerateArray()
                .SelectMany(row => row.EnumerateArray().Select(value => value.GetSingle())).ToArray();
            Assert.Equal(expected, output);
        }
    }

    [Fact]
    public void DegenerateChannelAndClippingUseFixedContract()
    {
        var stats = new[]
        {
            new FixedScalerStatistic(1, 0, 1, 1),
            new FixedScalerStatistic(0, 1, -1, 1),
        };
        var scaler = new FixedScaler(1, "ohlcv_minmax", 1, Array.Empty<int>(), 1.0,
            new[] { "constant", "variable" }, stats, "fixture");
        var output = new float[2];
        scaler.Transform(new double[] { 20, 5 }, output);
        Assert.Equal(new float[] { 0, 1 }, output);
    }
}
