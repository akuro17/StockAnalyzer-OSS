using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The two producers of weekly and monthly bars must agree. The C# <see cref="TimeFrameAggregator"/> builds the bars of the chart and of the MCP tools;
/// <c>generate_timeframes.py</c> writes <c>Data/Weekly</c> and <c>Data/Monthly</c>, which the screener, scatter plot, backtest and training read.
/// <para>
/// Contract proved here: for the same daily bars both produce the same periods and the same open, high, low, close and volume. They differ ONLY in the
/// date label: the aggregator dates a bar by its first TRADING day, the Python files by the first CALENDAR day of the period (the Monday of the ISO
/// week, the 1st of the month). The two labels are equal when that day traded and differ after a holiday.
/// </para>
/// The Python side is the real script run in the repository's own <c>.venv</c> (opt-in, like the other child-process tests).
/// </summary>
[Collection("ChildProcess")]
public sealed class TimeframeGenerationParityTests
{
    private const string Symbol = "PARITY";

    // Days on which the exchange of the series is closed: they leave weeks without a Monday, a week with one traded day, and months that start mid-week.
    private static readonly HashSet<DateTime> Holidays = new[]
    {
        "2024-11-28", "2024-12-25", "2025-01-01", "2025-01-13", "2025-02-03", "2025-02-04", "2025-02-05", "2025-02-06", "2025-02-17", "2025-03-03", "2025-03-04",
    }.Select(DateTime.Parse).ToHashSet();

    /// <summary>Quarter steps only, so every price is exactly representable as float32 and as decimal.</summary>
    private static List<CandleData> DailySeries()
    {
        var bars = new List<CandleData>();
        decimal close = 150m;
        int i = 0;
        for (DateTime day = new(2024, 11, 4); day <= new DateTime(2025, 4, 30); day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || Holidays.Contains(day))
            {
                continue;
            }

            decimal open = close + ((i * 31 % 5) - 2) * 0.25m;
            close = open + ((i * 7919 % 9) - 4) * 0.25m;
            decimal high = Math.Max(open, close) + (i * 13 % 4) * 0.25m;
            decimal low = Math.Min(open, close) - (i * 17 % 4) * 0.25m;
            bars.Add(new CandleData(day, open, high, low, close, 1000 + i * 37 % 500));
            i++;
        }

        return bars;
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }

    private static CandleData ReadBar(JsonElement bar) => new(
        DateTime.ParseExact(bar.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        bar.GetProperty("open").GetDecimal(), bar.GetProperty("high").GetDecimal(), bar.GetProperty("low").GetDecimal(),
        bar.GetProperty("close").GetDecimal(), bar.GetProperty("volume").GetInt64());

    private static async Task<(List<CandleData> Weekly, List<CandleData> Monthly)> GenerateWithPythonAsync(List<CandleData> daily)
    {
        string root = RepositoryRoot();
        string python = Path.Combine(root, "StockAnalyzer.Python", ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python)) throw SkipException.ForSkip("The training Python environment is not installed.");

        string workdir = Path.Combine(Path.GetTempPath(), "sa_tf_parity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        try
        {
            string barsPath = Path.Combine(workdir, "bars.json");
            var json = new StringBuilder("[");
            json.Append(string.Join(",", daily.Select(bar => string.Create(CultureInfo.InvariantCulture,
                $"{{\"date\":\"{bar.Timestamp:yyyy-MM-dd}\",\"open\":{bar.Open},\"high\":{bar.High},\"low\":{bar.Low},\"close\":{bar.Close},\"volume\":{bar.Volume}}}"))));
            json.Append(']');
            File.WriteAllText(barsPath, json.ToString());

            var start = new ProcessStartInfo(python) { WorkingDirectory = root, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assets", "timeframe_generation_dump.py"));
            start.ArgumentList.Add("--python-dir");
            start.ArgumentList.Add(Path.Combine(root, "StockAnalyzer.Python"));
            start.ArgumentList.Add("--bars");
            start.ArgumentList.Add(barsPath);
            start.ArgumentList.Add("--workdir");
            start.ArgumentList.Add(Path.Combine(workdir, "tree"));
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            string output = await TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromMinutes(2));

            using JsonDocument result = JsonDocument.Parse(output.Split('\n').Last(line => line.TrimStart().StartsWith('{')));
            return (result.RootElement.GetProperty("weekly").EnumerateArray().Select(ReadBar).ToList(),
                result.RootElement.GetProperty("monthly").EnumerateArray().Select(ReadBar).ToList());
        }
        finally
        {
            try { Directory.Delete(workdir, true); } catch (IOException) { }
        }
    }

    /// <summary>Everything but the date label.</summary>
    private static void AssertSameValues(IReadOnlyList<CandleData> expected, IReadOnlyList<CandleData> actual, string frame)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            string at = $"{frame} #{i} (aggregator {expected[i].Timestamp:yyyy-MM-dd}, python {actual[i].Timestamp:yyyy-MM-dd})";
            Assert.True(expected[i].Open == actual[i].Open, $"open of {at}");
            Assert.True(expected[i].High == actual[i].High, $"high of {at}");
            Assert.True(expected[i].Low == actual[i].Low, $"low of {at}");
            Assert.True(expected[i].Close == actual[i].Close, $"close of {at}");
            Assert.True(expected[i].Volume == actual[i].Volume, $"volume of {at}");
        }
    }

    [ChildProcessFact]
    public async Task WeeklyAndMonthlyBarsOfThePythonFilesEqualTheAggregatorInOpenHighLowCloseVolume()
    {
        List<CandleData> daily = DailySeries();
        IReadOnlyList<CandleData> weekly = TimeFrameAggregator.AggregateDailyToWeekly(daily);
        IReadOnlyList<CandleData> monthly = TimeFrameAggregator.AggregateDailyToMonthly(daily);

        (List<CandleData> pythonWeekly, List<CandleData> pythonMonthly) = await GenerateWithPythonAsync(daily);

        Assert.True(weekly.Count > 20 && monthly.Count == 6, "the series must cover a year boundary, several months and many weeks");
        AssertSameValues(weekly, pythonWeekly, "weekly");
        AssertSameValues(monthly, pythonMonthly, "monthly");
    }

    [ChildProcessFact]
    public async Task TheDateLabelIsTheFirstTradingDayInTheAggregatorAndTheFirstCalendarDayOfThePeriodInThePythonFiles()
    {
        List<CandleData> daily = DailySeries();
        IReadOnlyList<CandleData> weekly = TimeFrameAggregator.AggregateDailyToWeekly(daily);
        IReadOnlyList<CandleData> monthly = TimeFrameAggregator.AggregateDailyToMonthly(daily);

        (List<CandleData> pythonWeekly, List<CandleData> pythonMonthly) = await GenerateWithPythonAsync(daily);

        for (int i = 0; i < weekly.Count; i++)
        {
            DateTime firstTradingDay = weekly[i].Timestamp.Date;
            Assert.Equal(daily.First(bar => bar.Timestamp.Date >= pythonWeekly[i].Timestamp.Date).Timestamp.Date, firstTradingDay); // aggregator = first trading day of the period
            Assert.Equal(DayOfWeek.Monday, pythonWeekly[i].Timestamp.DayOfWeek);                                                      // python = the Monday of the ISO week
            Assert.Equal(firstTradingDay.AddDays(-((int)firstTradingDay.DayOfWeek + 6) % 7), pythonWeekly[i].Timestamp.Date);
        }

        for (int i = 0; i < monthly.Count; i++)
        {
            Assert.Equal(new DateTime(monthly[i].Timestamp.Year, monthly[i].Timestamp.Month, 1), pythonMonthly[i].Timestamp.Date);   // python = the 1st of the month
        }

        // The series was built so that the labels really differ somewhere (a week without its Monday, a month that starts mid-week).
        Assert.Contains(Enumerable.Range(0, weekly.Count), i => weekly[i].Timestamp.Date != pythonWeekly[i].Timestamp.Date);
        Assert.Contains(Enumerable.Range(0, monthly.Count), i => monthly[i].Timestamp.Date != pythonMonthly[i].Timestamp.Date);
        Assert.Contains(Enumerable.Range(0, weekly.Count), i => weekly[i].Timestamp.Date == pythonWeekly[i].Timestamp.Date);
    }
}
