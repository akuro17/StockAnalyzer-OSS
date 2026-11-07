using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Sliding-endDate prefix-invariance harness (plan task T17) and the divergence confirmation-time contract (plan task T1).
/// <para>
/// The older "mutate the bars after endDate" tests cannot see look-ahead inside the Core detectors, because the loader cuts the daily history
/// at endDate before any detector runs. The harness here asks the question the other way round: for the same stored data and two end dates
/// t1 &lt; t2, every event that the call at t2 reports as available on or before t1 must also be reported by the call at t1, and nothing else.
/// </para>
/// Hard assertions are made only for tools whose events are causal by design; the divergence tool is covered by the two invariants the owner
/// approved (R-1) plus a logged characterization of the full set comparison, which the nearest-pivot matching (decision D-2) can break.
/// </summary>
public sealed class SlidingEndDatePrefixInvarianceTests : IDisposable
{
    private const string Symbol = "WAVE";
    private const int Days = 420;
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private readonly ITestOutputHelper _output;
    private readonly ReadOnlyDataTreeFixture _fixture;
    private readonly List<McpServerRuntime> _runtimes = new();

    public SlidingEndDatePrefixInvarianceTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture = ReadOnlyDataTreeFixture.Create(1);
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, WaveCloses());
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

    /// <summary>A trend plus three incommensurable waves, so price and RSI both form many swing highs and lows that do not line up.</summary>
    private static IReadOnlyList<decimal> WaveCloses()
    {
        var closes = new List<decimal>(Days);
        uint state = 12345;
        for (int i = 0; i < Days; i++)
        {
            // A fixed linear congruential generator keeps the noise identical on every machine; the noise moves RSI swings away from price swings.
            state = unchecked(state * 1103515245u + 12345u) & 0x7fffffffu;
            double noise = ((state % 2001u) - 1000u) / 1000.0 * 1.6;
            double value = 100.0 + 12.0 * Math.Sin(i * 0.21) + 6.0 * Math.Sin(i * 0.067 + 1.0) + 3.0 * Math.Sin(i * 0.9) + 0.03 * i + noise;
            closes.Add(Math.Round((decimal)value, 2));
        }

        return closes;
    }

    private static DateOnly DateOf(int barIndex) => DateOnly.FromDateTime(FirstDay.AddDays(barIndex));

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string text) => DateOnly.FromDateTime(DateTime.Parse(text, CultureInfo.InvariantCulture));

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

    // ---- harness -----------------------------------------------------------------------------------------------------------------

    /// <summary>One reported event: an identity that must not change between calls, and the day from which it is available to a user.</summary>
    private sealed record Observed(string Key, DateOnly AvailableOn);

    private sealed record PrefixDiff(int ShortCount, int LongFilteredCount, IReadOnlyList<string> OnlyInShort, IReadOnlyList<string> OnlyInLong)
    {
        public bool IsInvariant => OnlyInShort.Count == 0 && OnlyInLong.Count == 0;

        public override string ToString() => $"short={ShortCount} long(<=t1)={LongFilteredCount} onlyShort=[{string.Join("; ", OnlyInShort.Take(5))}] onlyLong=[{string.Join("; ", OnlyInLong.Take(5))}]";
    }

    private static PrefixDiff Compare(IReadOnlyList<Observed> atT1, IReadOnlyList<Observed> atT2, DateOnly t1)
    {
        HashSet<string> shortKeys = atT1.Select(o => o.Key).ToHashSet();
        HashSet<string> longKeys = atT2.Where(o => o.AvailableOn <= t1).Select(o => o.Key).ToHashSet();
        return new PrefixDiff(
            shortKeys.Count,
            longKeys.Count,
            shortKeys.Except(longKeys).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            longKeys.Except(shortKeys).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    private async Task<IReadOnlyList<Observed>> RunAsync(McpMessageDispatcher dispatcher, string tool, string arguments, Func<JsonElement, IEnumerable<Observed>> read)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement structured = McpTestSupport.Structured(response);
        // A truncated answer would drop events at one end date and not at the other, which is not what is being measured.
        Assert.False(structured.TryGetProperty("truncated", out JsonElement truncated) && truncated.GetBoolean(), $"{tool} answer was truncated");
        return read(structured).ToList();
    }

    private static readonly int[] CutBars = { 150, 250, 330 };

    private async Task AssertInvariantAsync(string tool, Func<string, string> arguments, Func<JsonElement, IEnumerable<Observed>> read, int minimumEvents)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        IReadOnlyList<Observed> full = await RunAsync(dispatcher, tool, arguments(string.Empty), read);
        Assert.True(full.Count >= minimumEvents, $"{tool}: the synthetic series produced {full.Count} events; the check would be vacuous (need >= {minimumEvents}).");

        foreach (int cutBar in CutBars)
        {
            DateOnly t1 = DateOf(cutBar);
            IReadOnlyList<Observed> atT1 = await RunAsync(dispatcher, tool, arguments($",\"endDate\":\"{Iso(t1)}\""), read);
            PrefixDiff diff = Compare(atT1, full, t1);
            _output.WriteLine($"{tool} t1={Iso(t1)}: {diff}");
            Assert.True(diff.IsInvariant, $"{tool} is not prefix-invariant at endDate {Iso(t1)}: {diff}");
        }
    }

    // ---- tools that are causal by design: hard assertions --------------------------------------------------------------------------

    [Fact]
    public Task ExtractSignalEvents_EveryTrueBar_IsPrefixInvariant()
        => AssertInvariantAsync(
            "sa_extract_signal_events",
            end => $$$"""{"symbol":"{{{Symbol}}}","timeFrame":"D1","condition":{"left":{"indicator":"RSI","parameters":{"Period":14}},"comparison":"LessThan","right":50},"startDate":"{{{Iso(DateOf(60))}}}","eventMode":"EveryTrueBar","order":"OldestFirst","maxEvents":200{{{end}}}}""",
            root => root.GetProperty("events").EnumerateArray()
                .Select(e => new Observed($"bar{e.GetProperty("barIndex").GetInt32()}", ParseDay(e.GetProperty("timestamp").GetString()!))),
            minimumEvents: 5);

    [Fact]
    public Task AnalyzeMarketStructure_PivotsAndShifts_ArePrefixInvariantByConfirmation()
        => AssertInvariantAsync(
            "sa_analyze_market_structure",
            end => $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","zigzagThresholdPercent":3.0,"outputTailCount":5000{{end}}}""",
            root =>
            {
                var observed = new List<Observed>();
                foreach (JsonElement p in root.GetProperty("pivots").EnumerateArray())
                {
                    // A pivot is available from the bar that confirms it, not from its own bar.
                    observed.Add(new Observed($"pivot{p.GetProperty("index").GetInt32()}", ParseDay(p.GetProperty("confirmedAtDate").GetString()!)));
                }

                foreach (JsonElement s in root.GetProperty("shifts").EnumerateArray())
                {
                    observed.Add(new Observed($"shift{s.GetProperty("type").GetString()}{s.GetProperty("index").GetInt32()}", ParseDay(s.GetProperty("date").GetString()!)));
                }

                return observed;
            },
            minimumEvents: 6);

    [Fact]
    public Task DetectCandlePatterns_ArePrefixInvariant()
        => AssertInvariantAsync(
            "sa_detect_candle_patterns",
            end => $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","windowBars":5000{{end}}}""",
            root => root.GetProperty("patterns").EnumerateArray()
                .Select(m => new Observed($"{m.GetProperty("index").GetInt32()}:{m.GetProperty("pattern").GetString()}", ParseDay(m.GetProperty("date").GetString()!))),
            minimumEvents: 3);

    // ---- sa_detect_divergences: confirmation-time contract (T1) -------------------------------------------------------------------

    private sealed record Divergence(string Key, string SignalType, int TriggerIndex, string Timestamp, int PriceEndIndex, int IndicatorEndIndex, int ConfirmedAtIndex, string ConfirmedAtTime, int LagBars);

    private async Task<(IReadOnlyList<Divergence> Events, int LoadedBarCount)> DivergencesAsync(McpMessageDispatcher dispatcher, int pivotOrder, string end)
    {
        string arguments = $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","indicator":"RSI","pivotOrder":{{pivotOrder}},"maxEvents":200{{end}}}""";
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_detect_divergences", arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement root = McpTestSupport.Structured(response);
        Assert.False(root.GetProperty("truncated").GetBoolean(), "sa_detect_divergences answer was truncated");
        var events = new List<Divergence>();
        foreach (JsonElement e in root.GetProperty("events").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "Divergence"))
        {
            string type = e.GetProperty("signalType").GetString()!;
            int priceStart = e.GetProperty("priceStartIndex").GetInt32();
            int priceEnd = e.GetProperty("priceEndIndex").GetInt32();
            int indicatorStart = e.GetProperty("indicatorStartIndex").GetInt32();
            int indicatorEnd = e.GetProperty("indicatorEndIndex").GetInt32();
            events.Add(new Divergence(
                $"{type}|{priceStart}|{priceEnd}|{indicatorStart}|{indicatorEnd}",
                type,
                e.GetProperty("triggerIndex").GetInt32(),
                e.GetProperty("timestamp").GetString()!,
                priceEnd,
                indicatorEnd,
                e.GetProperty("confirmedAtIndex").GetInt32(),
                e.GetProperty("confirmedAtTime").GetString()!,
                e.GetProperty("confirmationLagBars").GetInt32()));
        }

        return (events, root.GetProperty("loadedBarCount").GetInt32());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Divergence_ConfirmedAtFields_FollowTheDefinition(int pivotOrder)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        (IReadOnlyList<Divergence> events, int loadedBarCount) = await DivergencesAsync(dispatcher, pivotOrder, string.Empty);

        Assert.True(events.Count >= 5, $"only {events.Count} divergences; the check would be vacuous");
        foreach (Divergence d in events)
        {
            // confirmedAt = later of the two second pivots + pivotOrder; the trigger is unchanged (decision D-1 = A).
            Assert.Equal(Math.Max(d.PriceEndIndex, d.IndicatorEndIndex) + pivotOrder, d.ConfirmedAtIndex);
            Assert.Equal(d.PriceEndIndex, d.TriggerIndex);
            Assert.Equal(d.ConfirmedAtIndex - d.TriggerIndex, d.LagBars);
            Assert.True(d.LagBars >= pivotOrder, $"{d.Key}: lag {d.LagBars} is shorter than pivotOrder {pivotOrder}");
            Assert.Equal(DateOf(d.TriggerIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00", d.Timestamp);
            Assert.Equal(DateOf(d.ConfirmedAtIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00", d.ConfirmedAtTime);
            Assert.True(d.ConfirmedAtIndex <= loadedBarCount - 1);
        }

        // The case where the indicator pivot lies after the price pivot, so the indicator pivot decides the confirmation bar.
        Assert.Contains(events, d => d.IndicatorEndIndex > d.PriceEndIndex);
    }

    [Fact]
    public async Task Divergence_NoEventIsConfirmedAfterEndDate()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        foreach (int cutBar in CutBars)
        {
            DateOnly t1 = DateOf(cutBar);
            (IReadOnlyList<Divergence> events, int loadedBarCount) = await DivergencesAsync(dispatcher, 3, $",\"endDate\":\"{Iso(t1)}\"");
            Assert.True(events.Count > 0, $"no divergence up to {Iso(t1)}; the check would be vacuous");
            Assert.Equal(cutBar + 1, loadedBarCount);
            foreach (Divergence d in events)
            {
                Assert.True(d.ConfirmedAtIndex <= cutBar, $"{d.Key} is confirmed at bar {d.ConfirmedAtIndex}, after endDate {Iso(t1)} (bar {cutBar})");
                Assert.True(ParseDay(d.ConfirmedAtTime) <= t1);
            }
        }
    }

    [Fact]
    public async Task Divergence_IsNotReportedOneBarBeforeItsConfirmation()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        (IReadOnlyList<Divergence> full, _) = await DivergencesAsync(dispatcher, 3, string.Empty);
        Assert.True(full.Count >= 10, $"only {full.Count} divergences; the check would be vacuous");

        // Every 4th event, so the test stays fast and still covers all four signal types of the series.
        foreach (Divergence d in full.Where((_, i) => i % 4 == 0))
        {
            (IReadOnlyList<Divergence> before, _) = await DivergencesAsync(dispatcher, 3, $",\"endDate\":\"{Iso(DateOf(d.ConfirmedAtIndex - 1))}\"");
            Assert.DoesNotContain(before, e => e.Key == d.Key);
        }
    }

    [Fact]
    public async Task Divergence_TriggerTimeIsNotWhenTheEventBecomesKnown_AndConfirmedAtIs()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        (IReadOnlyList<Divergence> full, _) = await DivergencesAsync(dispatcher, 3, string.Empty);

        int lookAheadByTrigger = 0;
        int onlyShortByConfirmed = 0;
        int onlyLongByConfirmed = 0;
        // Cut one bar after each trigger: at that end date the event is "triggered" but its pivots are not yet confirmed.
        foreach (int cutBar in full.Select(d => d.TriggerIndex + 1).Distinct().Take(10))
        {
            DateOnly t1 = DateOf(cutBar);
            (IReadOnlyList<Divergence> atT1, _) = await DivergencesAsync(dispatcher, 3, $",\"endDate\":\"{Iso(t1)}\"");

            IReadOnlyList<Observed> shortObserved = atT1.Select(d => new Observed(d.Key, ParseDay(d.ConfirmedAtTime))).ToList();
            IReadOnlyList<Observed> longByTrigger = full.Select(d => new Observed(d.Key, ParseDay(d.Timestamp))).ToList();
            IReadOnlyList<Observed> longByConfirmed = full.Select(d => new Observed(d.Key, ParseDay(d.ConfirmedAtTime))).ToList();

            PrefixDiff byTrigger = Compare(shortObserved, longByTrigger, t1);
            PrefixDiff byConfirmed = Compare(shortObserved, longByConfirmed, t1);
            _output.WriteLine($"divergences t1={Iso(t1)} by trigger time:      {byTrigger}");
            _output.WriteLine($"divergences t1={Iso(t1)} by confirmedAt time:  {byConfirmed}");

            lookAheadByTrigger += byTrigger.OnlyInLong.Count;
            onlyShortByConfirmed += byConfirmed.OnlyInShort.Count;
            onlyLongByConfirmed += byConfirmed.OnlyInLong.Count;
        }

        // Reading triggerIndex as "known on that day" is wrong: events whose trigger day is <= t1 are missing from the call that ends at t1.
        // This is the look-ahead the confirmedAt fields make visible; it is the reason they exist.
        Assert.True(lookAheadByTrigger > 0, "expected events whose trigger day is on or before endDate but that only appear in a later call");

        // The same comparison by confirmedAt time is only logged, not asserted: the nearest-pivot matching of the Core detector (decision D-2,
        // unchanged) can swap the indicator pivot of an earlier divergence once a closer pivot is confirmed later.
        _output.WriteLine($"divergences by confirmedAt over the cut dates: onlyShort={onlyShortByConfirmed} onlyLong={onlyLongByConfirmed}; by trigger time: look-ahead events={lookAheadByTrigger}");
    }
}
