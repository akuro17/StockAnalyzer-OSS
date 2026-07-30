using Moq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System.IO;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Moq-based tests verifying that Python-dependent indicators
/// handle external communication errors gracefully (Graceful Degradation).
/// Covers: MESA, EGARCH, StructuralDTW.
/// </summary>
public class PythonIndicatorErrorPathTests
{
    private static List<CoreCandleData> CreateTestCandles(int count = 30)
    {
        var startDate = DateTime.Today;
        return Enumerable.Range(0, count).Select(i => new CoreCandleData(
            startDate.AddDays(i), 100 + i, 102 + i, 98 + i, 100 + i, 1000
        )).ToList();
    }

    // =====================================================================
    // 1. PythonService is null — IExecutionContext returns null
    // =====================================================================

    [Fact]
    public async Task Mesa_PythonServiceNull_ReturnsFailure()
    {
        var indicator = new CoreMesaIndicator();
        var context = new CoreExecutionContext(pythonService: null);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("IPythonService", result.ErrorMessage);
    }

    [Fact]
    public async Task Egarch_PythonServiceNull_ReturnsFailure()
    {
        var indicator = new CoreEgarchIndicator();
        var context = new CoreExecutionContext(pythonService: null);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("IPythonService", result.ErrorMessage);
    }

    [Fact]
    public async Task StructuralDtw_PythonServiceNull_ReturnsFailure()
    {
        var indicator = new CoreStructuralDtwIndicator();
        var context = new CoreExecutionContext(pythonService: null);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("IPythonService", result.ErrorMessage);
    }

    // =====================================================================
    // 2. PythonService throws exception during communication
    // =====================================================================

    [Fact]
    public async Task Mesa_SendCandlesThrows_ReturnsFailure()
    {
        var mock = new Mock<IPythonService>();
        mock.Setup(s => s.InitializeExternalProcessAsync()).Returns(Task.CompletedTask);
        mock.Setup(s => s.SendCandlesAsync(It.IsAny<List<CandleData>>()))
            .ThrowsAsync(new InvalidOperationException("Python process crashed"));
        // ExecuteTransactionAsync has a default interface implementation that just calls the action,
        // so it will propagate the exception from SendCandlesAsync.
        mock.Setup(s => s.ExecuteTransactionAsync(It.IsAny<Func<Task<string>>>()))
            .Returns<Func<Task<string>>>(async action => await action());

        var indicator = new CoreMesaIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Python process crashed", result.ErrorMessage);
    }

    [Fact]
    public async Task Egarch_SendCandlesThrows_ReturnsFailure()
    {
        var mock = new Mock<IPythonService>();
        mock.Setup(s => s.InitializeExternalProcessAsync()).Returns(Task.CompletedTask);
        mock.Setup(s => s.SendCandlesAsync(It.IsAny<List<CandleData>>()))
            .ThrowsAsync(new TimeoutException("Connection timed out"));
        mock.Setup(s => s.ExecuteTransactionAsync(It.IsAny<Func<Task<string>>>()))
            .Returns<Func<Task<string>>>(async action => await action());

        var indicator = new CoreEgarchIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Connection timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task StructuralDtw_SendCandlesThrows_ReturnsFailure()
    {
        var mock = new Mock<IPythonService>();
        mock.Setup(s => s.InitializeExternalProcessAsync()).Returns(Task.CompletedTask);
        mock.Setup(s => s.SendCandlesAsync(It.IsAny<List<CandleData>>()))
            .ThrowsAsync(new IOException("Broken pipe"));
        mock.Setup(s => s.ExecuteTransactionAsync(It.IsAny<Func<Task<string>>>()))
            .Returns<Func<Task<string>>>(async action => await action());

        var indicator = new CoreStructuralDtwIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Broken pipe", result.ErrorMessage);
    }

    // =====================================================================
    // 3. Python returns error status JSON: {"status": "error", "error": "..."}
    // =====================================================================

    [Fact]
    public async Task Mesa_ErrorJsonResponse_ReturnsFailure()
    {
        var errorJson = "{\"status\":\"error\",\"error\":\"MESA convergence failed\"}";
        var mock = CreateMockWithResponse(
            s => s.CalculateMesaAsync(It.IsAny<decimal>(), It.IsAny<decimal>()),
            errorJson);

        var indicator = new CoreMesaIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("MESA convergence failed", result.ErrorMessage);
    }

    [Fact]
    public async Task Egarch_ErrorJsonResponse_ReturnsFailure()
    {
        var errorJson = "{\"status\":\"error\",\"error\":\"Insufficient data for EGARCH(1,1)\"}";
        var mock = CreateMockWithResponse(
            s => s.CalculateEgarchAsync(It.IsAny<int>(), It.IsAny<int>()),
            errorJson);

        var indicator = new CoreEgarchIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
    }

    [Fact]
    public async Task StructuralDtw_ErrorJsonResponse_ReturnsFailure()
    {
        var errorJson = "{\"status\":\"error\",\"error\":\"DTW matrix allocation failed\"}";
        var mock = CreateMockWithResponse(
            s => s.CalculateStructuralDtwOscillatorAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            errorJson);

        var indicator = new CoreStructuralDtwIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
        Assert.Contains("DTW matrix allocation failed", result.ErrorMessage);
    }

    // =====================================================================
    // 4. Python returns unparseable / invalid JSON
    // =====================================================================

    [Fact]
    public async Task Mesa_InvalidJson_ReturnsFailure()
    {
        var mock = CreateMockWithResponse(
            s => s.CalculateMesaAsync(It.IsAny<decimal>(), It.IsAny<decimal>()),
            "NOT VALID JSON {{{");

        var indicator = new CoreMesaIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task Egarch_InvalidJson_ReturnsFailure()
    {
        var mock = CreateMockWithResponse(
            s => s.CalculateEgarchAsync(It.IsAny<int>(), It.IsAny<int>()),
            "<html>502 Bad Gateway</html>");

        var indicator = new CoreEgarchIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task StructuralDtw_InvalidJson_ReturnsFailure()
    {
        var mock = CreateMockWithResponse(
            s => s.CalculateStructuralDtwOscillatorAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            "");

        var indicator = new CoreStructuralDtwIndicator();
        var context = new CoreExecutionContext(mock.Object);

        var result = await indicator.CalculateAsync(CreateTestCandles(), context);

        Assert.False(result.IsSuccessful);
    }

    // =====================================================================
    // Helper: Create a mock IPythonService that returns a specific response
    // =====================================================================

    private static Mock<IPythonService> CreateMockWithResponse(
        System.Linq.Expressions.Expression<Func<IPythonService, Task<string>>> calculationSetup,
        string response)
    {
        var mock = new Mock<IPythonService>();
        mock.Setup(s => s.InitializeExternalProcessAsync()).Returns(Task.CompletedTask);
        mock.Setup(s => s.SendCandlesAsync(It.IsAny<List<CandleData>>()))
            .ReturnsAsync("{\"status\":\"transfer_complete\"}");
        mock.Setup(calculationSetup).ReturnsAsync(response);
        mock.Setup(s => s.ExecuteTransactionAsync(It.IsAny<Func<Task<string>>>()))
            .Returns<Func<Task<string>>>(async action => await action());
        return mock;
    }
}
