using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Tests for the native C# (DTW-based) PatternRecognitionService. No Python service is involved.
/// </summary>
public class PatternRecognitionServiceTests
{
    private const int WindowLength = 40;
    private static readonly double[] DoubleBottomTemplate = { 0.0, -1.0, -0.3, -1.0, 0.0 };

    /// <summary>Builds candles whose Close linearly interpolates the template over <paramref name="length"/> bars.</summary>
    private static List<CandleData> CreateTemplateCandles(double[] template, int length, double scale = 10.0, double offset = 100.0)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / (length - 1) * (template.Length - 1);
            int lo = (int)t;
            int hi = Math.Min(lo + 1, template.Length - 1);
            double v = template[lo] + (t - lo) * (template[hi] - template[lo]);
            decimal close = (decimal)(offset + scale * v);
            candles.Add(new CandleData(DateTime.Today.AddDays(i), close, close, close, close, 1000));
        }
        return candles;
    }

    private static PatternRecognitionService CreateService() => new();

    [Fact]
    public async Task DetectAsync_ExactTemplateShape_DetectsPatternWithFullProbability()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, WindowLength, WindowLength, 5, 0.5);

        Assert.True(result.IsSuccessful);
        var top = result.Patterns[0];
        Assert.Equal("DoubleBottom", top.Name);
        Assert.Equal(1.0, top.Probability, 3);
        Assert.Equal(0, top.StartIndex);
        Assert.Equal(WindowLength - 1, top.EndIndex);
    }

    [Fact]
    public async Task DetectAsync_ScaleAndOffsetInvariant()
    {
        var baseline = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);
        var scaled = CreateTemplateCandles(DoubleBottomTemplate, WindowLength, scale: 300.0, offset: 5000.0);

        var a = await CreateService().DetectAsync(baseline, WindowLength, WindowLength, 5, 0.0);
        var b = await CreateService().DetectAsync(scaled, WindowLength, WindowLength, 5, 0.0);

        Assert.Equal(a.Patterns.Select(p => p.Name), b.Patterns.Select(p => p.Name));
        for (int i = 0; i < a.Patterns.Count; i++)
        {
            Assert.Equal(a.Patterns[i].Probability, b.Patterns[i].Probability, 3);
        }
    }

    [Fact]
    public async Task DetectAsync_ResultsSortedByProbabilityDescending()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, WindowLength, WindowLength, 5, 0.0);

        Assert.True(result.IsSuccessful);
        Assert.Equal(6, result.Patterns.Count);
        Assert.Equal(
            result.Patterns.OrderByDescending(p => p.Probability).Select(p => p.Probability),
            result.Patterns.Select(p => p.Probability));
    }

    [Fact]
    public async Task DetectAsync_ThresholdAboveAllProbabilities_ReturnsEmpty()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, WindowLength, WindowLength, 5, 1.01);

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public async Task DetectAsync_FlatPrices_ReturnsEmpty()
    {
        var candles = Enumerable.Range(0, 80)
            .Select(i => new CandleData(DateTime.Today.AddDays(i), 100m, 100m, 100m, 100m, 1000))
            .ToList();

        var result = await CreateService().DetectAsync(candles);

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public async Task DetectAsync_InsufficientData_ReturnsEmptySuccess()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, 5);

        var result = await CreateService().DetectAsync(candles, minWindow: 20);

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public async Task DetectAsync_InvalidWindowStep_ReturnsFailure()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, WindowLength, WindowLength, windowStep: 0);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task DetectAsync_MaxWindowBelowMinWindow_ReturnsFailure()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, minWindow: 30, maxWindow: 20);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task DetectAsync_NegativeShortSpanPenaltyAlpha_ReturnsFailure()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, WindowLength, WindowLength, 5, 0.5, shortSpanPenaltyAlpha: -0.5);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task DetectAsync_NegativeWarpingRadius_ReturnsFailure()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, WindowLength, WindowLength, 5, 0.5, warpingRadius: DtwMath.UnconstrainedRadius - 1);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task DetectAsync_WithWarpingRadius_StillDetectsExactShape()
    {
        var candles = CreateTemplateCandles(DoubleBottomTemplate, WindowLength);

        var result = await CreateService().DetectAsync(candles, WindowLength, WindowLength, 5, 0.5, warpingRadius: 3);

        Assert.True(result.IsSuccessful);
        Assert.Equal("DoubleBottom", result.Patterns[0].Name);
    }
}
