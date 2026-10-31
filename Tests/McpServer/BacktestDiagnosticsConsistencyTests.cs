using System.Text.Json;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The diagnostics must agree with what a client could already reconstruct from the trades: the documented substitute
/// (sa_ai_integration_0010 equity_curve_substitute) is equity.initial plus the closedNet of each trade in response order, and it equals
/// equity.final only when every trade is returned and no position is open on the last processed bar.
/// </summary>
public sealed class BacktestDiagnosticsConsistencyTests : IDisposable
{
    private const string AllDiagnostics = ",\"includeEquityCurve\":\"Bar\",\"includeDrawdownEpisodes\":5,\"includePeriodicReturns\":true";

    private readonly BacktestDiagnosticsTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task WhenNoPositionIsOpenOnTheLastBar_TheCurveTheTradeFlowAndTheYearlyReturnAgree()
    {
        var dispatcher = await _host.StartAsync();

        // Ending the period on the exit day of a trade leaves no position open (a new entry cannot fill on the exit bar).
        JsonElement probe = BacktestDiagnosticsTestHost.Result(await BacktestDiagnosticsTestHost.RunAsync(dispatcher, BacktestDiagnosticsTestHost.Period));
        string lastExit = probe.GetProperty("trades").GetProperty("items")[2].GetProperty("exitDate").GetString()!;
        string period = $",\"startDate\":\"2024-02-01\",\"endDate\":\"{lastExit}\"";

        JsonElement result = BacktestDiagnosticsTestHost.Result(await BacktestDiagnosticsTestHost.RunAsync(dispatcher, period + AllDiagnostics));

        JsonElement trades = result.GetProperty("trades");
        Assert.False(trades.GetProperty("truncated").GetBoolean());
        decimal initial = result.GetProperty("equity").GetProperty("initial").GetDecimal();
        decimal final = result.GetProperty("equity").GetProperty("final").GetDecimal();
        decimal flow = initial + trades.GetProperty("items").EnumerateArray().Sum(t => t.GetProperty("closedNet").GetDecimal());
        Assert.Equal(lastExit, trades.GetProperty("items").EnumerateArray().Last().GetProperty("exitDate").GetString());

        // The substitute equals equity.final, and the curve ends at the same number.
        Assert.Equal(final, flow);
        JsonElement[] points = result.GetProperty("equityCurve").GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(final, points[^1].GetProperty("equity").GetDecimal());
        Assert.Equal(lastExit, points[^1].GetProperty("date").GetString());

        // Every exit day of the curve holds the realized flow so far, because the position is flat right after an exit fill.
        decimal running = initial;
        foreach (JsonElement trade in trades.GetProperty("items").EnumerateArray())
        {
            running += trade.GetProperty("closedNet").GetDecimal();
            string exitDate = trade.GetProperty("exitDate").GetString()!;
            Assert.Equal(running, points.Single(p => p.GetProperty("date").GetString() == exitDate).GetProperty("equity").GetDecimal());
        }

        // The yearly return is the same two numbers as a ratio.
        JsonElement yearly = Assert.Single(result.GetProperty("periodicReturns").GetProperty("yearly").EnumerateArray());
        Assert.Equal(flow / initial - 1m, yearly.GetProperty("returnRatio").GetDecimal(), 12);
    }

    [Fact]
    public async Task EveryCurveResolutionEndsAtTheSameEquityAsTheRunReports()
    {
        var dispatcher = await _host.StartAsync();

        foreach (string resolution in new[] { "Bar", "Weekly", "Monthly" })
        {
            JsonElement result = BacktestDiagnosticsTestHost.Result(await BacktestDiagnosticsTestHost.RunAsync(
                dispatcher, BacktestDiagnosticsTestHost.Period + $",\"includeEquityCurve\":\"{resolution}\""));

            JsonElement last = result.GetProperty("equityCurve").GetProperty("points").EnumerateArray().Last();
            Assert.Equal(result.GetProperty("equity").GetProperty("final").GetDecimal(), last.GetProperty("equity").GetDecimal());
            Assert.Equal(result.GetProperty("period").GetProperty("endDate").GetString(), last.GetProperty("date").GetString());
        }
    }
}
