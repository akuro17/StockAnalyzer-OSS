using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Guards that the offline Python feature pipeline
/// (<c>StockAnalyzer.Python/training/dataset.py</c>) and the shipped C#
/// <see cref="MLDataProcessor"/> produce the same feature tensors for the same raw
/// OHLCV window, so a model trained on the Python features runs on identical inputs
/// at inference time.
///
/// Vectors are regenerated with:
/// <c>python StockAnalyzer.Python/training/dataset.py --emit-parity-vectors
/// Tests/StockAnalyzer.Core.Tests/Assets/feature_parity_vectors.json</c>.
/// Python computes in float64 and C# in float32, so the tolerance is a
/// float32-rounding budget (<see cref="ParityAtol"/>), not zero.
/// </summary>
public class MLDataProcessorParityTests
{
    // float32-vs-float64 rounding budget; matches the ONNX export tolerance used
    // elsewhere in the training tooling (ONNX_ATOL = 1e-4).
    private const float ParityAtol = 1e-4f;
    private const int FeaturesPerCandle = 5;

    private readonly MLDataProcessor _processor = new();

    private sealed record ParityVector(
        string Name,
        int Window,
        double[] Open,
        double[] High,
        double[] Low,
        double[] Close,
        long[] Volume,
        float[] OhlcvMinMax,
        float[] LogReturn,
        float[] Zscore,
        float[] ZscoreJoint,
        float[] LogReturnOhlc);

    private static readonly IReadOnlyList<ParityVector> Vectors = LoadVectors();

    private static string AssetPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "feature_parity_vectors.json");

    private static IReadOnlyList<ParityVector> LoadVectors()
    {
        if (!File.Exists(AssetPath))
        {
            throw new FileNotFoundException(
                "Parity vectors missing. Regenerate with: python " +
                "StockAnalyzer.Python/training/dataset.py --emit-parity-vectors " +
                "Tests/StockAnalyzer.Core.Tests/Assets/feature_parity_vectors.json",
                AssetPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(AssetPath));
        var list = new List<ParityVector>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new ParityVector(
                el.GetProperty("name").GetString()!,
                el.GetProperty("window").GetInt32(),
                Doubles(el, "open"),
                Doubles(el, "high"),
                Doubles(el, "low"),
                Doubles(el, "close"),
                Longs(el, "volume"),
                Floats(el, "ohlcv_minmax"),
                Floats(el, "log_return"),
                Floats(el, "zscore"),
                Floats(el, "zscore_joint"),
                Floats(el, "log_return_ohlc")));
        }
        return list;
    }

    private static double[] Doubles(JsonElement el, string name) =>
        el.GetProperty(name).EnumerateArray().Select(x => x.GetDouble()).ToArray();

    private static long[] Longs(JsonElement el, string name) =>
        el.GetProperty(name).EnumerateArray().Select(x => x.GetInt64()).ToArray();

    private static float[] Floats(JsonElement el, string name) =>
        el.GetProperty(name).EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();

    public static IEnumerable<object[]> VectorIndices()
    {
        for (int i = 0; i < Vectors.Count; i++)
        {
            yield return new object[] { i, Vectors[i].Name };
        }
    }

    private static List<CandleData> BuildCandles(ParityVector v)
    {
        var candles = new List<CandleData>(v.Window);
        var t0 = new DateTime(2024, 1, 1);
        for (int i = 0; i < v.Window; i++)
        {
            candles.Add(new CandleData(
                t0.AddDays(i),
                (decimal)v.Open[i],
                (decimal)v.High[i],
                (decimal)v.Low[i],
                (decimal)v.Close[i],
                v.Volume[i]));
        }
        return candles;
    }

    [Fact]
    public void ParityVectorFile_IsPresentAndNonEmpty()
    {
        Assert.True(File.Exists(AssetPath), $"missing parity vector asset: {AssetPath}");
        Assert.NotEmpty(Vectors);
    }

    [Theory]
    [MemberData(nameof(VectorIndices))]
    public void OhlcvMinMax_MatchesPython(int index, string name)
    {
        var v = Vectors[index];
        var candles = BuildCandles(v);

        var actual = new float[v.Window * FeaturesPerCandle];
        _processor.NormalizeCandles(candles, 0, v.Window, actual);

        AssertClose(v.OhlcvMinMax, actual, $"{name} ohlcv_minmax");
    }

    [Theory]
    [MemberData(nameof(VectorIndices))]
    public void LogReturn_MatchesPython(int index, string name)
    {
        var v = Vectors[index];
        var candles = BuildCandles(v);

        var actual = new float[v.Window];
        _processor.ComputeLogReturns(candles, 0, v.Window, actual);

        AssertClose(v.LogReturn, actual, $"{name} log_return");
    }

    [Theory]
    [MemberData(nameof(VectorIndices))]
    public void ZScoreOhlcv_MatchesPython(int index, string name)
    {
        var v = Vectors[index];
        var candles = BuildCandles(v);

        var actual = new float[v.Window * FeaturesPerCandle];
        _processor.NormalizeZScoreOhlcv(candles, 0, v.Window, actual);

        AssertClose(v.Zscore, actual, $"{name} zscore");
    }

    [Theory]
    [MemberData(nameof(VectorIndices))]
    public void ZScoreJointOhlcv_MatchesPython(int index, string name)
    {
        var v = Vectors[index];
        var candles = BuildCandles(v);

        var actual = new float[v.Window * FeaturesPerCandle];
        _processor.ComputeJointZScoreOhlcv(candles, 0, v.Window, actual);

        AssertClose(v.ZscoreJoint, actual, $"{name} zscore_joint");
    }

    [Theory]
    [MemberData(nameof(VectorIndices))]
    public void LogReturnOhlc_MatchesPython(int index, string name)
    {
        const int LogReturnOhlcChannels = 4;
        var v = Vectors[index];
        var candles = BuildCandles(v);

        var actual = new float[v.Window * LogReturnOhlcChannels];
        _processor.ComputeLogReturnsOhlc(candles, 0, v.Window, actual);

        AssertClose(v.LogReturnOhlc, actual, $"{name} log_return_ohlc");
    }

    private static void AssertClose(float[] expected, float[] actual, string label)
    {
        Assert.Equal(expected.Length, actual.Length);

        float worst = 0f;
        int worstAt = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            float d = MathF.Abs(expected[i] - actual[i]);
            if (d > worst)
            {
                worst = d;
                worstAt = i;
            }
        }

        Assert.True(
            worst <= ParityAtol,
            $"{label}: max |delta| {worst:E3} at index {worstAt} " +
            $"(python {expected[worstAt]:E6}, csharp {actual[worstAt]:E6}) exceeds {ParityAtol:E0}");
    }
}
