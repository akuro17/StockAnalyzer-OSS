using System.Text.Json;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T5 (decision D-5): the pure statistics of <see cref="EventStudySummary"/> on hand-computed fixtures. Returns are ratios (0.05 = 5%).
/// </summary>
public sealed class EventStudySummaryTests
{
    private const decimal Tolerance = 0.000000000001m;

    private static decimal?[] Close1 => new decimal?[] { 0.10m, -0.05m, 0.02m, 0.00m, 0.03m };

    // The event with index 1 has no 5-bar horizon.
    private static decimal?[] Close5 => new decimal?[] { 0.20m, null, 0.10m, -0.10m, 0.00m };

    [Fact]
    public void FiveEventsOneHorizon_AreTheHandComputedFigures_ZeroIsNotPositive()
    {
        EventStudySummary.Stats stats = EventStudySummary.Summarize(Close1, eventCount: 5);

        Assert.Equal(5, stats.Count);
        Assert.Equal(0, stats.MissingCount);
        Assert.Equal(0.02m, stats.Mean);          // (0.10 - 0.05 + 0.02 + 0 + 0.03) / 5
        Assert.Equal(0.02m, stats.Median);        // sorted -0.05, 0, 0.02, 0.03, 0.10
        Assert.Equal(-0.05m, stats.Min);
        Assert.Equal(0.10m, stats.Max);
        Assert.Equal(0.6m, stats.PositiveRatio);  // 0.10, 0.02, 0.03 of 5; the 0.00 is not positive
        // Population variance (divisor n): (0.08^2 + 0.07^2 + 0 + 0.02^2 + 0.01^2) / 5 = 0.00236
        Assert.InRange(Math.Abs(stats.StdDev!.Value - (decimal)Math.Sqrt(0.00236)), 0m, Tolerance);
    }

    [Fact]
    public void AnEventWithoutTheHorizonIsMissing_AndLeavesTheOtherFourFigures()
    {
        EventStudySummary.Stats stats = EventStudySummary.Summarize(Close5, eventCount: 5);

        Assert.Equal(4, stats.Count);
        Assert.Equal(1, stats.MissingCount);
        Assert.Equal(0.05m, stats.Mean);          // 0.20 / 4
        Assert.Equal(0.05m, stats.Median);        // type 7 of -0.10, 0, 0.10, 0.20 = (0 + 0.10) / 2
        Assert.Equal(-0.10m, stats.Min);
        Assert.Equal(0.20m, stats.Max);
        Assert.Equal(0.5m, stats.PositiveRatio);  // 0.20 and 0.10 of 4
        Assert.InRange(Math.Abs(stats.StdDev!.Value - (decimal)Math.Sqrt(0.0125)), 0m, Tolerance); // deviations 0.15, 0.05, -0.15, -0.05
    }

    [Fact]
    public void NoEvents_GiveZeroCountsAndNullFigures()
    {
        EventStudySummary.Stats stats = EventStudySummary.Summarize(Array.Empty<decimal?>(), eventCount: 0);

        Assert.Equal(new EventStudySummary.Stats(0, 0, null, null, null, null, null, null), stats);
    }

    [Fact]
    public void EveryValueMissing_GivesCountZeroMissingEqualToTheEventCountAndNullFigures()
    {
        EventStudySummary.Stats stats = EventStudySummary.Summarize(new decimal?[] { null, null, null }, eventCount: 3);

        Assert.Equal(new EventStudySummary.Stats(0, 3, null, null, null, null, null, null), stats);
    }

    [Fact]
    public void OneValue_HasNoStdDev_ButEveryOtherFigure()
    {
        EventStudySummary.Stats stats = EventStudySummary.Summarize(new decimal?[] { -0.04m, null }, eventCount: 2);

        Assert.Equal(1, stats.Count);
        Assert.Equal(1, stats.MissingCount);
        Assert.Equal(-0.04m, stats.Mean);
        Assert.Equal(-0.04m, stats.Median);
        Assert.Null(stats.StdDev);
        Assert.Equal(-0.04m, stats.Min);
        Assert.Equal(-0.04m, stats.Max);
        Assert.Equal(0m, stats.PositiveRatio);
    }

    [Fact]
    public void ADuplicateOfEveryValue_KeepsTheMeanAndTheRatio_AndDoublesTheCount()
    {
        decimal?[] doubled = Close1.Concat(Close1).ToArray();

        EventStudySummary.Stats stats = EventStudySummary.Summarize(doubled, eventCount: 10);

        Assert.Equal(10, stats.Count);
        Assert.Equal(0.02m, stats.Mean);
        Assert.Equal(0.6m, stats.PositiveRatio);
    }

    [Fact]
    public void TheJson_HasTheFixedKeysInOrder_ForEveryHorizonAndBothBases_WithNullsWhereThereIsNoValue()
    {
        var fromClose = new List<IReadOnlyDictionary<string, decimal?>>();
        var fromNextOpen = new List<IReadOnlyDictionary<string, decimal?>>();
        for (int i = 0; i < 5; i++)
        {
            fromClose.Add(new Dictionary<string, decimal?> { ["1"] = Close1[i], ["5"] = Close5[i] });
            fromNextOpen.Add(new Dictionary<string, decimal?> { ["1"] = null, ["5"] = null });
        }

        string json = ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            EventStudySummary.Write(writer, new[] { 1, 5 }, fromClose, fromNextOpen);
            writer.WriteEndObject();
        });

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement study = document.RootElement.GetProperty("eventStudy");
        Assert.Equal(new[] { "eventCount", "overlapNote", "lookAheadDiagnostic", "fromClose", "fromNextOpen" }, study.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(5, study.GetProperty("eventCount").GetInt32());
        Assert.Equal(EventStudySummary.OverlapNote, study.GetProperty("overlapNote").GetString());
        Assert.True(study.GetProperty("lookAheadDiagnostic").GetBoolean());
        Assert.Equal(new[] { "1", "5" }, study.GetProperty("fromClose").EnumerateObject().Select(p => p.Name).ToArray());

        JsonElement h5 = study.GetProperty("fromClose").GetProperty("5");
        Assert.Equal(new[] { "count", "missingCount", "mean", "median", "stdDev", "min", "max", "positiveRatio" }, h5.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(4, h5.GetProperty("count").GetInt32());
        Assert.Equal(1, h5.GetProperty("missingCount").GetInt32());
        Assert.Equal(0.05m, h5.GetProperty("mean").GetDecimal());

        JsonElement open1 = study.GetProperty("fromNextOpen").GetProperty("1");
        Assert.Equal(0, open1.GetProperty("count").GetInt32());
        Assert.Equal(5, open1.GetProperty("missingCount").GetInt32());
        Assert.All(new[] { "mean", "median", "stdDev", "min", "max", "positiveRatio" }, key => Assert.Equal(JsonValueKind.Null, open1.GetProperty(key).ValueKind));
    }
}
