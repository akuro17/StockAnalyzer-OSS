using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.ScreeningConditions;

public class PatternMatchConditionTests
{
    private const int WindowLength = 40;

    /// <summary>Close prices follow a double-bottom shape (0, -1, -0.3, -1, 0) stretched over the window.</summary>
    private static List<CandleData> CreateDoubleBottomCandles()
    {
        double[] template = { 0.0, -1.0, -0.3, -1.0, 0.0 };
        var candles = new List<CandleData>();
        for (int i = 0; i < WindowLength; i++)
        {
            double t = (double)i / (WindowLength - 1) * (template.Length - 1);
            int lo = (int)t;
            int hi = Math.Min(lo + 1, template.Length - 1);
            decimal close = (decimal)(100.0 + 10.0 * (template[lo] + (t - lo) * (template[hi] - template[lo])));
            candles.Add(new CandleData(DateTime.Today.AddDays(i), close, close, close, close, 1000));
        }
        return candles;
    }

    [Fact]
    public async Task IsMetAsync_DetectsPattern_ReturnsTrue()
    {
        var condition = new PatternMatchCondition(new PatternRecognitionService(), "DoubleBottom", 0.8, WindowLength, WindowLength);

        Assert.True(await condition.IsMetAsync(CreateDoubleBottomCandles()));
    }

    [Fact]
    public async Task IsMetAsync_AnyPattern_ReturnsTrue()
    {
        var condition = new PatternMatchCondition(new PatternRecognitionService(), null, 0.8, WindowLength, WindowLength);

        Assert.True(await condition.IsMetAsync(CreateDoubleBottomCandles()));
    }

    [Fact]
    public async Task IsMetAsync_NoPatterns_ReturnsFalse()
    {
        var flat = Enumerable.Range(0, WindowLength)
            .Select(i => new CandleData(DateTime.Today.AddDays(i), 100m, 100m, 100m, 100m, 1000))
            .ToList();
        var condition = new PatternMatchCondition(new PatternRecognitionService(), "DoubleTop", 0.5, WindowLength, WindowLength);

        Assert.False(await condition.IsMetAsync(flat));
    }

    [Fact]
    public async Task IsMetAsync_LowProbabilityPattern_ReturnsFalse()
    {
        // A double-bottom shape is far from an inverse head-and-shoulders at a strict threshold.
        var condition = new PatternMatchCondition(new PatternRecognitionService(), "HeadAndShoulders", 0.99, WindowLength, WindowLength);

        Assert.False(await condition.IsMetAsync(CreateDoubleBottomCandles()));
    }

    [Fact]
    public async Task IsMetAsync_InsufficientCandles_ReturnsFalse()
    {
        var condition = new PatternMatchCondition(new PatternRecognitionService(), "DoubleBottom", 0.5, WindowLength, WindowLength);

        Assert.False(await condition.IsMetAsync(CreateDoubleBottomCandles().Take(5).ToList()));
    }

    [Fact]
    public async Task IsMetAsync_PassesConfiguredWindowParametersToService()
    {
        var recorder = new RecordingPatternService();
        var condition = new PatternMatchCondition(recorder, "DoubleBottom", 0.7, 25, 45, 3);

        await condition.IsMetAsync(CreateDoubleBottomCandles());

        Assert.Equal((25, 45, 3, 0.7), recorder.LastArguments);
    }

    private sealed class RecordingPatternService : IPatternRecognitionService
    {
        public (int MinWindow, int MaxWindow, int WindowStep, double Threshold) LastArguments { get; private set; }

        public Task<PatternRecognitionResult> DetectAsync(
            IReadOnlyList<CandleData> candles, int minWindow, int maxWindow, int windowStep,
            double threshold, int warpingRadius, double shortSpanPenaltyAlpha)
        {
            LastArguments = (minWindow, maxWindow, windowStep, threshold);
            return Task.FromResult(PatternRecognitionResult.Success(Array.Empty<DetectedPattern>()));
        }
    }
}
