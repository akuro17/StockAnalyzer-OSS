using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The weekly and monthly files are read by the screener, scatter plot, backtest and training, so a failed generation after the daily update must reach
/// the sync result (the sync item turns to Error). Only the decision is tested here; no Python process is started.
/// </summary>
public sealed class PythonServiceTimeFrameGenerationTests
{
    private sealed class FakeGenerator : ITimeFrameParquetGenerator
    {
        private readonly Func<string?, CancellationToken, int> _generate;
        public FakeGenerator(Func<string?, CancellationToken, int> generate) => _generate = generate;
        public string? RequestedSymbol { get; private set; }

        public Task<int> GenerateAsync(string? symbol = null, CancellationToken cancellationToken = default)
        {
            RequestedSymbol = symbol;
            return Task.FromResult(_generate(symbol, cancellationToken));
        }

        public Task<bool> GenerateForSymbolAsync(string symbol, IReadOnlyList<CandleData> dailyBars, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = new();
        public void Report(int value) => Values.Add(value);
    }

    private static PythonService CreateService(ITimeFrameParquetGenerator? generator)
        => new(new PythonIpcTestSettingsBase(), logger: null, timeframeGenerator: generator);

    [Fact]
    public async Task WithoutAGenerator_NothingFails()
    {
        await using var service = CreateService(null);

        Assert.Null(await service.GenerateTimeFramesAsync("AAA", null, CancellationToken.None));
    }

    [Fact]
    public async Task AGenerationWithoutFailures_ReturnsNoFailure_AndReportsProgress()
    {
        var generator = new FakeGenerator((_, _) => 0);
        var progress = new RecordingProgress();
        await using var service = CreateService(generator);

        Assert.Null(await service.GenerateTimeFramesAsync("AAA", progress, CancellationToken.None));
        Assert.Equal("AAA", generator.RequestedSymbol);
        Assert.Single(progress.Values);
    }

    [Fact]
    public async Task FailedTickers_AreReturnedAsAFailureThatNamesTheCount()
    {
        await using var service = CreateService(new FakeGenerator((_, _) => 3));

        string? failure = await service.GenerateTimeFramesAsync(null, null, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("3 ticker(s)", failure);
        Assert.Contains("all symbols", failure);
    }

    [Fact]
    public async Task AnExceptionOfTheGenerator_IsReturnedAsAFailureWithItsMessage()
    {
        await using var service = CreateService(new FakeGenerator((_, _) => throw new InvalidOperationException("disk full")));

        string? failure = await service.GenerateTimeFramesAsync("AAA", null, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("disk full", failure);
        Assert.Contains("AAA", failure);
    }

    [Fact]
    public async Task ACancellation_IsNotAFailureAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var service = CreateService(new FakeGenerator((_, token) => { token.ThrowIfCancellationRequested(); return 0; }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateTimeFramesAsync("AAA", null, cts.Token));
    }
}
