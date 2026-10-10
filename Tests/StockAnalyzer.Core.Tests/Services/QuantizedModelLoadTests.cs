using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// T17: the shipped C# ONNX Runtime must load and run a model produced by <c>verify_model.py --quantize</c>
/// (dynamic QInt8 quantization). Fixtures and tolerance come from <c>Assets/generate_quantized_fixture.py</c>; the tolerance is
/// the Python owner's default, read from the fixture report rather than repeated here. Two pairs are covered: the MatMul head
/// model (standard-domain ops) and an LSTM model, whose quantized graph uses the <c>com.microsoft</c> contrib op
/// <c>DynamicQuantizeLSTM</c> that only this runtime can prove it executes.
/// </summary>
[Collection("Non-Parallel ONNX Tests")]
public class QuantizedModelLoadTests
{
    private const string MatMulStem = "trend_predictor_goodmeta";
    private const string LstmStem = "trend_predictor_lstm";
    private const string SharpLstmStem = "trend_predictor_lstm_sharp";
    private const string LstmContribOp = "DynamicQuantizeLSTM";
    private const int FixtureWindowSize = 10;
    private const int CandleCount = 20;
    private const float SumTolerance = 1e-4f; // = IMLDataProcessor.SoftmaxSumTolerance

    private static string FloatPath(string stem) => Path.Combine("Assets", stem + ".onnx");
    private static string QuantizedPath(string stem) => Path.Combine("Assets", stem + "_quantized.onnx");

    private static JsonElement ReadReport(string stem)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine("Assets", stem + "_quantized.quantization.json")));
        return document.RootElement.Clone();
    }

    private static void AssertReportIsBoundToTheSource(string stem)
    {
        var report = ReadReport(stem);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FloatPath(stem)))).ToLowerInvariant();

        Assert.Equal(Path.GetFileName(FloatPath(stem)), report.GetProperty("source").GetString());
        Assert.Equal(sha, report.GetProperty("source_sha256").GetString());
        Assert.True(report.GetProperty("quantized_compute_nodes").EnumerateObject().Any(),
            "the fixture must contain quantized compute nodes");
    }

    private static async Task AssertQuantizedModelRunsWithinTheReportedTolerance(string stem)
    {
        var tolerance = ReadReport(stem).GetProperty("max_prob_delta").GetSingle();
        var candles = PredictionServiceTests.BuildCandles(CandleCount);
        using var floatService = new PredictionService(
            new PredictionServiceTests.TestSettings(FloatPath(stem), FixtureWindowSize), new MLDataProcessor());
        using var quantizedService = new PredictionService(
            new PredictionServiceTests.TestSettings(QuantizedPath(stem), FixtureWindowSize), new MLDataProcessor());

        var expected = await floatService.PredictAsync(candles);
        var actual = await quantizedService.PredictAsync(candles);

        Assert.False(expected.IsFallback);
        Assert.False(actual.IsFallback, "the quantized model must load through the contract validation and run");
        Assert.Equal(expected.Scores.Select(s => s.Label), actual.Scores.Select(s => s.Label));
        Assert.Equal(1f, actual.Scores.Sum(s => s.Score), SumTolerance);
        for (var i = 0; i < expected.Scores.Count; i++)
        {
            Assert.InRange(Math.Abs(expected.Scores[i].Score - actual.Scores[i].Score), 0f, tolerance);
        }
    }

    [Fact]
    public void FixtureReport_IsBoundToTheCommittedSourceModel() => AssertReportIsBoundToTheSource(MatMulStem);

    [Fact]
    public Task PredictAsync_QuantizedModel_RunsAndStaysWithinTheReportedToleranceOfTheFloatModel()
        => AssertQuantizedModelRunsWithinTheReportedTolerance(MatMulStem);

    [Fact]
    public void LstmFixtureReport_IsBoundToTheCommittedSourceModelAndNamesTheContribOp()
    {
        AssertReportIsBoundToTheSource(LstmStem);
        var nodes = ReadReport(LstmStem).GetProperty("quantized_compute_nodes");
        Assert.True(nodes.TryGetProperty(LstmContribOp, out var count) && count.GetInt32() >= 1,
            $"the LSTM fixture must contain {LstmContribOp}");
    }

    [Fact]
    public Task PredictAsync_QuantizedLstmModel_RunsAndStaysWithinTheReportedToleranceOfTheFloatModel()
        => AssertQuantizedModelRunsWithinTheReportedTolerance(LstmStem);

    /// <summary>
    /// The candle series the Python search measured, read from the asset the fixture generator writes
    /// (<c>generate_quantized_fixture.write_csharp_candles</c>): the closed-form formulas have a single owner, the generator.
    /// </summary>
    private static List<List<CandleData>> LoadFixtureSeries(string stem, int expectedSeries, int expectedBars)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine("Assets", stem + ".candles.json")));
        var all = new List<List<CandleData>>();
        foreach (var series in document.RootElement.GetProperty("series").EnumerateArray())
        {
            var candles = new List<CandleData>(expectedBars);
            var day = 0;
            foreach (var bar in series.EnumerateArray())
            {
                var v = bar.EnumerateArray().Select(e => e.GetDouble()).ToArray(); // open, high, low, close, volume
                candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(day++), (decimal)v[0], (decimal)v[1], (decimal)v[2], (decimal)v[3], (long)v[4]));
            }

            Assert.Equal(expectedBars, candles.Count);
            all.Add(candles);
        }

        Assert.Equal(expectedSeries, all.Count);
        return all;
    }

    private static string TopLabel(PredictionResult result) => result.Scores.OrderByDescending(s => s.Score).First().Label;

    [Fact]
    public void SharpLstmFixtureReport_IsBoundToTheCommittedSourceModelAndRecordsTheCriteria()
    {
        AssertReportIsBoundToTheSource(SharpLstmStem);
        var report = ReadReport(SharpLstmStem);
        Assert.True(report.GetProperty("quantized_compute_nodes").TryGetProperty(LstmContribOp, out var count) && count.GetInt32() >= 1);
        var criteria = report.GetProperty("criteria");
        Assert.True(report.GetProperty("measured_python_batch1").GetProperty("median_max_prob").GetDouble() >= criteria.GetProperty("min_median_max_prob").GetDouble());
        Assert.True(report.GetProperty("measured_python_batch1").GetProperty("argmax_agreement").GetDouble() >= criteria.GetProperty("min_python_argmax_agreement").GetDouble());
    }

    [Fact]
    public async Task PredictAsync_QuantizedSharpLstmModel_KeepsTheTopClassOfTheFloatModel()
    {
        // FX1 A / FX2 A: the probability tolerance of the other pairs does not apply (a sharper head amplifies probability differences); what
        // matters here is that the predicted class survives the quantization, on windows whose top class is not the same for every window.
        var report = ReadReport(SharpLstmStem);
        var criteria = report.GetProperty("criteria");
        var series = report.GetProperty("csharp_series").GetInt32();
        var bars = report.GetProperty("series_bars").GetInt32();
        var window = report.GetProperty("window").GetInt32(); // the model window the generator built the pair with (one owner)
        var minAgreement = criteria.GetProperty("min_csharp_argmax_agreement").GetDouble();
        var minShare = criteria.GetProperty("min_class_share").GetDouble();
        var minClasses = criteria.GetProperty("min_classes_at_share").GetInt32();
        using var floatService = new PredictionService(
            new PredictionServiceTests.TestSettings(FloatPath(SharpLstmStem), window), new MLDataProcessor());
        using var quantizedService = new PredictionService(
            new PredictionServiceTests.TestSettings(QuantizedPath(SharpLstmStem), window), new MLDataProcessor());

        var floatTop = new List<string>();
        var agreed = 0;
        foreach (var candles in LoadFixtureSeries(SharpLstmStem, series, bars))
        {
            var expected = await floatService.PredictAsync(candles);
            var actual = await quantizedService.PredictAsync(candles);
            Assert.False(expected.IsFallback);
            Assert.False(actual.IsFallback, "the quantized model must load through the contract validation and run");
            Assert.Equal(1f, actual.Scores.Sum(s => s.Score), SumTolerance);
            floatTop.Add(TopLabel(expected));
            if (TopLabel(expected) == TopLabel(actual))
            {
                agreed++;
            }
        }

        var used = floatTop.GroupBy(label => label).Count(group => (double)group.Count() / series >= minShare);
        Assert.True(used >= minClasses, string.Create(CultureInfo.InvariantCulture,
            $"the float model must predict at least {minClasses} classes for >= {minShare:P0} of the windows, got {used}"));
        Assert.True((double)agreed / series >= minAgreement, string.Create(CultureInfo.InvariantCulture,
            $"quantized vs float top class agreement {agreed}/{series} is below {minAgreement:P0}"));
    }
}
