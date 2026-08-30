using Moq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Tests verifying the resilience infrastructure:
/// - CircuitState model and event args
/// - PythonUnavailableException behavior
/// - Graceful Degradation when circuit breaker is open (all Python indicators)
/// </summary>
public class ResiliencePipelineTests
{
    // =====================================================================
    // 1. CircuitState model and event args
    // =====================================================================

    [Fact]
    public void CircuitState_DefaultValue_IsClosed()
    {
        // The default circuit state should be the healthy state
        var state = default(CircuitState);
        Assert.Equal(CircuitState.Closed, state);
    }

    [Theory]
    [InlineData(CircuitState.Closed, CircuitState.Open)]
    [InlineData(CircuitState.Open, CircuitState.HalfOpen)]
    [InlineData(CircuitState.HalfOpen, CircuitState.Closed)]
    public void CircuitStateChangedEventArgs_PreservesTransitionInfo(CircuitState oldState, CircuitState newState)
    {
        var ex = new IOException("test failure");
        var args = new CircuitStateChangedEventArgs(oldState, newState, ex);

        Assert.Equal(oldState, args.OldState);
        Assert.Equal(newState, args.NewState);
        Assert.Same(ex, args.TriggeringException);
    }

    [Fact]
    public void CircuitStateChangedEventArgs_AllowsNullException()
    {
        var args = new CircuitStateChangedEventArgs(CircuitState.Open, CircuitState.HalfOpen);

        Assert.Null(args.TriggeringException);
        Assert.Equal(CircuitState.Open, args.OldState);
        Assert.Equal(CircuitState.HalfOpen, args.NewState);
    }

    // =====================================================================
    // 2. PythonUnavailableException behavior
    // =====================================================================

    [Fact]
    public void PythonUnavailableException_DefaultMessage_ContainsCircuitBreaker()
    {
        var ex = new PythonUnavailableException();
        Assert.Contains("circuit breaker", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PythonUnavailableException_CustomMessage_IsPreserved()
    {
        var ex = new PythonUnavailableException("Custom degradation message");
        Assert.Equal("Custom degradation message", ex.Message);
    }

    [Fact]
    public void PythonUnavailableException_InnerException_IsPreserved()
    {
        var inner = new InvalidOperationException("Pipe broken");
        var ex = new PythonUnavailableException("Outer message", inner);

        Assert.Equal("Outer message", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    // =====================================================================
    // 3. Graceful Degradation: Indicators handle PythonUnavailableException
    //    by returning IndicatorResult.Failure with circuit breaker message
    // =====================================================================

    private static List<CoreCandleData> CreateTestCandles(int count = 30)
    {
        var startDate = DateTime.Today;
        return Enumerable.Range(0, count).Select(i => new CoreCandleData(
            startDate.AddDays(i), 100 + i, 102 + i, 98 + i, 100 + i, 1000
        )).ToList();
    }

    private static Mock<IPythonService> CreateMockThrowingUnavailable()
    {
        var mock = new Mock<IPythonService>();
        // InitializeExternalProcessAsync succeeds (called before try in MESA/EGARCH)
        mock.Setup(s => s.InitializeExternalProcessAsync()).Returns(Task.CompletedTask);
        // ExecuteTransactionAsync throws (called inside try block in all indicators)
        mock.Setup(s => s.ExecuteTransactionAsync(It.IsAny<Func<Task<string>>>()))
            .ThrowsAsync(new PythonUnavailableException(
                "Python service is temporarily unavailable (circuit breaker open). Feature will auto-recover when service is restored."));
        return mock;
    }

    [Fact]
    public async Task Mesa_CircuitBreakerOpen_ReturnsGracefulDegradation()
    {
        var mock = CreateMockThrowingUnavailable();
        var indicator = new CoreMesaIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("circuit breaker", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auto-recover", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Egarch_CircuitBreakerOpen_ReturnsGracefulDegradation()
    {
        var mock = CreateMockThrowingUnavailable();
        var indicator = new CoreEgarchIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("circuit breaker", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auto-recover", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StructuralDtw_CircuitBreakerOpen_ReturnsGracefulDegradation()
    {
        var mock = CreateMockThrowingUnavailable();
        var indicator = new CoreStructuralDtwIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("circuit breaker", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auto-recover", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // 4. IResilienceStateProvider contract
    // =====================================================================

    // =====================================================================
    // 5. Pipeline composition: circuit breaker must be outermost, wrapping retry
    //    (regression for a bug where each individual retry of ONE failing call was
    //    independently counted by the circuit breaker, so a single transient failure
    //    could exhaust MinimumThroughput and trip the breaker by itself)
    // =====================================================================

    private class AlwaysFailingSettings : IStockAnalyzerSettings
    {
        public string? PythonPath => null;
        public string PythonScriptDirectory => "";
        public string PythonServerScriptName => "";
        public int PythonMaxRetries => 3;
        public int PythonBackoffMs => 1;
        public int PythonHealthCheckIntervalMs => 100000;
        public int PipeConnectPollIntervalMs => 100;
        public int SyncTimeoutMinutes => 1;
        public IReadOnlyList<string> PythonEssentialPackages => new List<string>();
        public int DisposeWaitMs => 100;
        public string DefaultSymbol => "MSFT";
        public string RenkoUpColor => "#00FF00";
        public string RenkoDownColor => "#FF0000";
        public string KagiUpColor => "#00FF00";
        public string KagiDownColor => "#FF0000";
        public string PnfUpColor => "#00FF00";
        public string PnfDownColor => "#FF0000";
        public string GetReverseWatchPhaseColor(int phase) => "#FFFFFF";
        public string? ScreeningDataPath => null;
        public IReadOnlyList<string> DefaultScreenerSymbols => new List<string>();
        public string PipeName => "resilienceorderpipe";
        public int PipeConnectionTimeoutMs => 5000;
        public int ScreenerMaxParallelism => 4;
        public decimal ZigzagThresholdPercent => 5m;
        public int PatternRecognitionMinWindow => 10;
        public int PatternRecognitionMaxWindow => 100;
        public int PatternRecognitionWindowStep => 5;
        public double PatternRecognitionDefaultThreshold => 0.5;
        public int CircuitBreakerMinimumThroughput => 3;
        public double CircuitBreakerFailureRatio => 0.5;
        public int CircuitBreakerBreakDurationMs => 30000;
        public int CircuitBreakerSamplingDurationMs => 60000;
        public string PredictionModelPath => "";
        public int PredictionWindowSize => 30;
        public StockAnalyzer.Core.Models.PredictionFeatureMode PredictionFeatureMode => StockAnalyzer.Core.Models.PredictionFeatureMode.OhlcvMinMax;
        public float PredictionConfidenceThreshold => 0.5f;
        public string? PredictionInputNodeName => null;
        public string? PredictionOutputNodeName => null;
        public System.Collections.Generic.IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
        public int PredictionRetryMaxAttempts => 3;
        public int PredictionRetryBaseDelayMs => 50;
        public int PredictionRetryMaxDelayMs => 500;
        public string? LocaleResourcePath => null;
    }

    [Fact]
    public async Task ResiliencePipeline_SingleFailingCall_DoesNotAloneTripCircuitBreaker()
    {
        // A single logical operation that always fails (so all 3 retries are exhausted)
        // must be recorded as ONE failure by the circuit breaker, not 3 -- otherwise
        // MinimumThroughput=3 is satisfied by this one call alone.
        var manager = new PythonProcessManager(new AlwaysFailingSettings());
        var pipelineField = typeof(PythonProcessManager).GetField("_resiliencePipeline", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pipeline = (Polly.ResiliencePipeline)pipelineField!.GetValue(manager)!;

        Task<int> AlwaysThrows() => throw new IOException("simulated transient failure");

        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync(async _ => await AlwaysThrows()));

        Assert.Equal(CircuitState.Closed, ((IResilienceStateProvider)manager).CurrentState);
    }

    [Fact]
    public async Task ResiliencePipeline_ThreeSeparateFailingCalls_TripsCircuitBreaker()
    {
        // Three SEPARATE logical calls (each exhausting its own retries) must accumulate
        // toward MinimumThroughput=3 and open the breaker -- proving retries-within-a-call
        // are isolated from the breaker's cross-call failure tracking.
        var manager = new PythonProcessManager(new AlwaysFailingSettings());
        var pipelineField = typeof(PythonProcessManager).GetField("_resiliencePipeline", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var pipeline = (Polly.ResiliencePipeline)pipelineField!.GetValue(manager)!;

        Task<int> AlwaysThrows() => throw new IOException("simulated transient failure");

        for (int i = 0; i < 3; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ => await AlwaysThrows());
            }
            catch (IOException) { }
            catch (Polly.CircuitBreaker.BrokenCircuitException) { }
        }

        Assert.Equal(CircuitState.Open, ((IResilienceStateProvider)manager).CurrentState);
    }

    [Fact]
    public void IResilienceStateProvider_MockCanImplement()
    {
        // Verify the interface is mockable and the contract is correct
        var mock = new Mock<IResilienceStateProvider>();
        mock.SetupGet(p => p.CurrentState).Returns(CircuitState.Open);

        CircuitStateChangedEventArgs? receivedArgs = null;
        mock.Object.StateChanged += (_, args) => receivedArgs = args;

        Assert.Equal(CircuitState.Open, mock.Object.CurrentState);

        // Raise event through mock
        mock.Raise(p => p.StateChanged += null, 
            new CircuitStateChangedEventArgs(CircuitState.Closed, CircuitState.Open, new IOException("test")));

        Assert.NotNull(receivedArgs);
        Assert.Equal(CircuitState.Closed, receivedArgs!.OldState);
        Assert.Equal(CircuitState.Open, receivedArgs.NewState);
    }
}
