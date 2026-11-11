using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// The per-type default parameter prototype of ScreenerValueExtractor must not change any value. The fingerprint of the values of EVERY
/// indicator type under four parameter variants was taken from the implementation that created a fresh instance and called
/// GetDefaultSettings for every condition (before the prototype cache), and is pinned here.
/// </summary>
public class ScreenerDefaultParameterCacheParityTests
{
    private readonly ITestOutputHelper _output;
    public ScreenerDefaultParameterCacheParityTests(ITestOutputHelper output) => _output = output;

    // SHA-256 (hex) of the text produced by BuildReport(), taken from the implementation before the prototype cache.
    private const string PinnedFingerprint = "A13FB4243A891F84FA56C5B728CDAE47D36A01AE0A9A7228455508A30F25F88B";

    private static List<CoreCandleData> Candles(int count)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + 10m * (decimal)Math.Sin(i * 0.3) + (i % 7) * 0.25m;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), price, price + 1.5m, price - 1.25m, price + 0.5m, 1000 + (i % 13) * 100));
        }
        return candles;
    }

    private static string BuildReport()
    {
        var candles = Candles(300);
        var extractor = new ScreenerValueExtractor();
        var sb = new StringBuilder();
        var nine = JsonDocument.Parse("9").RootElement;

        // Order matters on purpose: parameters, then none, then parameters again on the same extractor and thread (a condition must not inherit
        // the Period of the one before it).
        var variants = new (string name, Func<Dictionary<string, object>> make)[]
        {
            ("period7", () => new Dictionary<string, object> { ["Period"] = 7 }),
            ("none", () => new Dictionary<string, object>()),
            ("period7+unknown", () => new Dictionary<string, object> { ["Period"] = 7, ["NoSuchKey"] = 1 }),
            ("json9", () => new Dictionary<string, object> { ["Period"] = nine }),
            ("none-again", () => new Dictionary<string, object>()),
        };

        foreach (IndicatorType type in Enum.GetValues(typeof(IndicatorType)))
        {
            foreach (var (name, make) in variants)
            {
                var config = new ScreenerIndicatorSideConfig { IndicatorType = type, Parameters = make() };
                string text;
                try
                {
                    var value = extractor.ExtractValueNullable(config, candles);
                    text = value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";
                }
                catch (Exception ex)
                {
                    text = "ex:" + ex.GetType().Name;
                }
                sb.Append(type).Append('|').Append(name).Append('|').Append(text).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string Fingerprint(string report) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(report)));

    [Fact]
    public void ValuesOfEveryIndicatorTypeAreUnchanged()
    {
        string report = BuildReport();
        string fingerprint = Fingerprint(report);
        _output.WriteLine("FINGERPRINT " + fingerprint);
        _output.WriteLine("LINES " + report.Count(c => c == '\n'));

        Assert.Equal(PinnedFingerprint, fingerprint);
    }

    [Fact]
    public void TheReportIsDeterministic()
    {
        Assert.Equal(Fingerprint(BuildReport()), Fingerprint(BuildReport()));
    }
}
