using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan tasks T2/T3: a geometric formation that has broken out must stay reported by <c>sa_detect_chart_patterns</c> once its last pivot is confirmed.
/// The Core detector rewrites <c>EndIndex</c> to the breakout bar; the tool looks that index up in the map of confirmed ZigZag pivots.
/// </summary>
public sealed class GeometricBreakoutConfirmationTests : IDisposable
{
    private const string Symbol = "BRK";
    private const decimal Threshold = 3.0m;
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private readonly ITestOutputHelper _output;
    private readonly ReadOnlyDataTreeFixture _fixture;
    private readonly List<McpServerRuntime> _runtimes = new();

    public GeometricBreakoutConfirmationTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture = ReadOnlyDataTreeFixture.Create(1);
        File.WriteAllText(_fixture.TickerListPath, JsonSerializer.Serialize(new[] { Symbol }));
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private sealed record Bar(decimal O, decimal H, decimal L, decimal C);

    /// <summary>
    /// A channel/triangle made of straight legs: highs start at 140 and move by <paramref name="upperSlope"/> per bar, lows start at 80 and move by
    /// <paramref name="lowerSlope"/> per bar, a turn every <paramref name="legLength"/> bars. After the last turn the price leaves the formation
    /// through the upper line (<paramref name="breakUp"/>) or the lower line, so the breakout bar is an ordinary bar and not a pivot.
    /// </summary>
    private static List<Bar> Build(double upperSlope, double lowerSlope, int legLength, int turns, bool breakUp)
    {
        var points = new List<(int Bar, decimal Price)> { (0, 110m) };
        int bar = 4;
        for (int j = 0; j < turns; j++)
        {
            bool high = j % 2 == 0;
            decimal price = high
                ? (decimal)(140.0 + upperSlope * (bar - 4))
                : (decimal)(80.0 + lowerSlope * (bar - 4 - legLength));
            points.Add((bar, Math.Round(price, 2)));
            bar += legLength;
        }

        (int lastBar, decimal lastPrice) = points[^1];
        bool lastWasHigh = (turns - 1) % 2 == 0;
        // leave the formation: from the last turn go to the far side beyond the opposite line, then continue
        decimal target = breakUp
            ? (decimal)(140.0 + upperSlope * (lastBar + 2 * legLength - 4)) + 12m
            : (decimal)(80.0 + lowerSlope * (lastBar + 2 * legLength - 4 - legLength)) - 12m;
        points.Add((lastBar + 2 * legLength, Math.Round(target, 2)));
        points.Add((lastBar + 3 * legLength, Math.Round(target + (breakUp ? -6m : 6m), 2)));
        _ = lastPrice;
        _ = lastWasHigh;

        var bars = new Bar[points[^1].Bar + 1];
        decimal previousClose = points[0].Price;
        for (int leg = 0; leg < points.Count - 1; leg++)
        {
            (int fromBar, decimal fromPrice) = points[leg];
            (int toBar, decimal toPrice) = points[leg + 1];
            for (int b = fromBar; b <= toBar; b++)
            {
                decimal close = Math.Round(fromPrice + (toPrice - fromPrice) * (b - fromBar) / (toBar - fromBar), 2);
                decimal open = b == 0 ? close : previousClose;
                bars[b] = new Bar(open, Math.Max(open, close) + 0.3m, Math.Min(open, close) - 0.3m, close);
                previousClose = close;
            }
        }

        return bars.ToList();
    }

    private static List<CandleData> ToCandles(IReadOnlyList<Bar> bars)
        => bars.Select((t, i) => new CandleData(FirstDay.AddDays(i), t.O, t.H, t.L, t.C, 1000)).ToList();

    private void WriteDailyParquet(IReadOnlyList<Bar> bars)
    {
        var rows = new List<string>();
        for (int i = 0; i < bars.Count; i++)
        {
            Bar b = bars[i];
            rows.Add(FormattableString.Invariant($"(DATE '{FirstDay.AddDays(i):yyyy-MM-dd}', {b.O}, {b.H}, {b.L}, {b.C}, 1000)"));
        }

        string path = Path.Combine(_fixture.DailyDirectory, $"{Symbol}.parquet").Replace("\\", "/");
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"COPY (SELECT * FROM (VALUES {string.Join(", ", rows)}) AS t(date, open, high, low, close, volume)) TO '{path}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static IEnumerable<(double Upper, double Lower, int Leg, int Turns, bool Up)> Cases()
    {
        foreach (double upper in new[] { -0.3, -0.5, -0.7, 0.0, 0.3 })
        foreach (double lower in new[] { 0.2, 0.4, 0.6, 0.0, -0.3 })
        foreach (int leg in new[] { 6, 8, 10 })
        foreach (int turns in new[] { 6, 7, 8, 9 })
        foreach (bool up in new[] { true, false })
        {
            yield return (upper, lower, leg, turns, up);
        }
    }

    [Fact]
    public void Core_LastPivotIndex_IsThePivotBeforeTheBreakoutBar()
    {
        int formations = 0, brokenOut = 0, brokenOutEndIsNotAPivot = 0;
        foreach (var c in Cases())
        {
            List<CandleData> candles = ToCandles(Build(c.Upper, c.Lower, c.Leg, c.Turns, c.Up));
            var pivotIndices = MarketStructureDetector.ExtractPivots(candles, Threshold).Select(p => p.Index).ToHashSet();
            foreach (DetectedFormation f in GeometricPatternDetector.Detect(candles, Threshold))
            {
                formations++;
                Assert.True(pivotIndices.Contains(f.LastPivotIndex), $"{f}: LastPivotIndex {f.LastPivotIndex} is not a pivot");
                Assert.InRange(f.LastPivotIndex, f.StartIndex, f.EndIndex);
                if (!f.IsBrokenOut)
                {
                    Assert.Equal(f.EndIndex, f.LastPivotIndex);
                    continue;
                }

                brokenOut++;
                Assert.True(f.LastPivotIndex < f.EndIndex, $"{f}: a broken-out formation ends after its last pivot");
                if (!pivotIndices.Contains(f.EndIndex))
                {
                    brokenOutEndIsNotAPivot++;
                }
            }
        }

        // The premise of the fix: the breakout bar is normally not a pivot, so EndIndex cannot be used to look up the confirmation of the formation.
        _output.WriteLine($"formations={formations} brokenOut={brokenOut} brokenOutWhoseEndIsNotAPivot={brokenOutEndIsNotAPivot}");
        Assert.True(brokenOut > 0 && brokenOutEndIsNotAPivot > 0, "the search produced no broken-out formation; the check would be vacuous");
    }


    private sealed record Reported(string Key, string Name, string Start, string End, string ConfirmedAt);

    private async Task<(IReadOnlyList<Reported> Patterns, int Excluded)> ToolAsync(McpMessageDispatcher dispatcher, string? end)
    {
        string endPart = end is null ? string.Empty : $",\"endDate\":\"{end}\"";
        string arguments = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","families":["Geometric"],"geometricZigZagThreshold":3.0{{endPart}}}""";
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_detect_chart_patterns", arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement root = McpTestSupport.Structured(response);
        var patterns = root.GetProperty("patterns").EnumerateArray()
            .Select(p => new Reported(
                $"{p.GetProperty("name").GetString()}@{p.GetProperty("startDate").GetString()}",
                p.GetProperty("name").GetString()!,
                p.GetProperty("startDate").GetString()!,
                p.GetProperty("endDate").GetString()!,
                p.GetProperty("confirmedAtDate").GetString()!))
            .ToList();
        return (patterns, root.GetProperty("excludedUnconfirmedCount").GetInt32());
    }

    [Theory]
    [InlineData(-0.3, 0.2, 6, 6, true, true)]
    [InlineData(-0.3, 0.2, 8, 8, true, true)]
    [InlineData(-0.3, 0.2, 6, 7, false, true)]
    // Characterization of decision Q05 (not fixed by T3): with more pivots the detector re-selects a longer fit and the earlier completed channel disappears.
    [InlineData(-0.3, 0.2, 10, 7, false, false)]
    public async Task BrokenOutFormation_IsReportedOnceItsLastPivotIsConfirmed_AndStaysReported(double upperSlope, double lowerSlope, int legLength, int turns, bool breakUp, bool stableAfterBreakout)
    {
        List<Bar> bars = Build(upperSlope, lowerSlope, legLength, turns, breakUp);
        WriteDailyParquet(bars);
        McpMessageDispatcher dispatcher = await StartAsync();

        var table = new List<(int EndBar, IReadOnlyList<Reported> Patterns, int Excluded)>();
        for (int endBar = 20; endBar < bars.Count; endBar++)
        {
            string end = FirstDay.AddDays(endBar).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            (IReadOnlyList<Reported> patterns, int excluded) = await ToolAsync(dispatcher, end);
            table.Add((endBar, patterns, excluded));
            _output.WriteLine($"endBar={endBar} {end}: excludedUnconfirmed={excluded} patterns=[{string.Join(" | ", patterns.Select(p => $"{p.Name} {p.Start}..{p.End} confirmedAt {p.ConfirmedAt}"))}]");
        }

        // Expected behaviour: every broken-out formation that the Core detector finds on the whole history, and whose pivots are all confirmed by
        // the last bar, is reported by the tool at the last end date. (The Core call is the reference only; it is the same data the tool loads.)
        List<CandleData> candles = ToCandles(bars);
        var confirmedAt = MarketStructureDetector.ExtractConfirmedPivots(candles, Threshold)
            .Where(p => p.ConfirmedAtIndex.HasValue)
            .ToDictionary(p => p.Index, p => p.ConfirmedAtIndex!.Value);
        List<DetectedFormation> brokenOut = GeometricPatternDetector.Detect(candles, Threshold).Where(f => f.IsBrokenOut).ToList();
        Assert.NotEmpty(brokenOut);
        foreach (DetectedFormation f in brokenOut)
        {
            _output.WriteLine($"Core: {f} start={f.StartTime:yyyy-MM-dd} end={f.EndTime:yyyy-MM-dd} endIsConfirmedPivot={confirmedAt.ContainsKey(f.EndIndex)}");
        }

        IReadOnlyList<Reported> atLastBar = table[^1].Patterns;
        var missing = brokenOut
            .Select(f => $"{f.Type}@{f.StartTime:yyyy-MM-dd}")
            .Where(key => !atLastBar.Any(p => p.Key == key))
            .ToList();
        Assert.True(missing.Count == 0, $"broken-out formations found by the Core detector but not reported (excludedUnconfirmedCount={table[^1].Excluded}): {string.Join("; ", missing)}");

        // Once a formation has broken out it is complete: a pattern that the tool reports while the Core detector (run on the same prefix of bars)
        // sees that formation as broken out must stay reported, by name and start date, for every later end date. Formations that are still forming
        // may be replaced when later pivots extend them; that is a different limitation (decision Q05, described in the tool documentation).
        var lost = new List<string>();
        for (int i = 0; i < table.Count; i++)
        {
            List<CandleData> prefix = candles.Take(table[i].EndBar + 1).ToList();
            HashSet<string> brokenOutAtT1 = GeometricPatternDetector.Detect(prefix, Threshold)
                .Where(f => f.IsBrokenOut)
                .Select(f => $"{f.Type}@{f.StartTime:yyyy-MM-dd}")
                .ToHashSet();
            DateOnly t1 = DateOnly.FromDateTime(FirstDay.AddDays(table[i].EndBar));
            foreach (Reported p in table[i].Patterns.Where(p => brokenOutAtT1.Contains(p.Key) && DateOnly.Parse(p.ConfirmedAt, CultureInfo.InvariantCulture) <= t1))
            {
                foreach (var later in table.Skip(i + 1).Where(t => !t.Patterns.Any(q => q.Key == p.Key)))
                {
                    lost.Add($"{p.Key} (broken out and reported at endBar {table[i].EndBar}) is missing at endBar {later.EndBar}");
                }
            }
        }

        _output.WriteLine($"lost broken-out patterns: {lost.Count}");
        if (stableAfterBreakout)
        {
            Assert.True(lost.Count == 0, string.Join(Environment.NewLine, lost.Take(8)));
        }
    }
}
