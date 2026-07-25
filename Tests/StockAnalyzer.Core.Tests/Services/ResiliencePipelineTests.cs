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
