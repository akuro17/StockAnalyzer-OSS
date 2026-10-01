using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Stops a real preprocessing call at a deterministic prediction boundary.</summary>
internal sealed class PredictionBarrierProcessor : IMLDataProcessor, IDisposable
{
    internal static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);
    private readonly MLDataProcessor _inner = new();
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ConcurrentQueue<decimal> _observedCloses = new();
    private TaskCompletionSource<bool> _started = NewSignal();
    private int _armed;

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Started => _started.Task;
    internal decimal[] ObservedCloses => _observedCloses.ToArray();

    internal void Arm()
    {
        _release.Reset();
        _started = NewSignal();
        _observedCloses.Clear();
        Volatile.Write(ref _armed, 1);
    }

    internal void Release() => _release.Set();

    public void NormalizeCandles(IReadOnlyList<CandleData> candles, int startIndex, int count,
        Span<float> destination)
    {
        _observedCloses.Enqueue(candles[^1].Close);
        if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            _started.TrySetResult(true);
            if (!_release.Wait(BarrierTimeout))
                throw new TimeoutException("Prediction preprocessing barrier was not released.");
        }
        _inner.NormalizeCandles(candles, startIndex, count, destination);
    }

    public float[] NormalizeCandles(IReadOnlyList<CandleData> candles) => _inner.NormalizeCandles(candles);
    public void NormalizeCandles(IReadOnlyList<CandleData> candles, Span<float> destination) =>
        _inner.NormalizeCandles(candles, destination);
    public void ComputeSoftmax(ReadOnlySpan<float> logits, Span<float> probabilities) =>
        _inner.ComputeSoftmax(logits, probabilities);
    public (float Confidence, float Entropy) ComputeConfidenceAndEntropy(ReadOnlySpan<float> probabilities) =>
        _inner.ComputeConfidenceAndEntropy(probabilities);
    public void ComputeLogReturns(IReadOnlyList<CandleData> candles, int startIndex, int count,
        Span<float> destination) => _inner.ComputeLogReturns(candles, startIndex, count, destination);
    public void ComputeLogReturnsOhlc(IReadOnlyList<CandleData> candles, int startIndex, int count,
        Span<float> destination) => _inner.ComputeLogReturnsOhlc(candles, startIndex, count, destination);
    public void ComputeZScore(ReadOnlySpan<float> values, Span<float> destination) =>
        _inner.ComputeZScore(values, destination);
    public void ComputeJointZScoreOhlcv(IReadOnlyList<CandleData> candles, int startIndex, int count,
        Span<float> destination) => _inner.ComputeJointZScoreOhlcv(candles, startIndex, count, destination);
    public void NormalizeZScoreOhlcv(IReadOnlyList<CandleData> candles, int startIndex, int count,
        Span<float> destination) => _inner.NormalizeZScoreOhlcv(candles, startIndex, count, destination);
    public void Dispose() => _release.Dispose();
}
