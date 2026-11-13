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
    // Re-pinned 2026-11-13 with owner approval (WebAiExpansion plan 01 T1): the fingerprint above was A13FB424...F88B. Compared with the commit before
    // the change, ONLY BandWidth, BollingerBandsRatio and VaR differ, and only in the variants that pass a Period (period7, period7+unknown, json9):
    // their default parameter object used to be a Period-only CoreSmaParameter that their Configure ignored, so the Period never reached them; now it does.
    // The variants without a Period are unchanged. ExplicitPeriod_ReachesBandWidthBollingerBandsRatioAndVaR_LikeAStandaloneCalculation pins the new values
    // against a standalone calculation with Period = 7 and 9.
    // SECOND re-pin, 2026-11-13 with owner approval (WebAiExpansion plan 02 T6): the fingerprint above was 743C2C1C...182E. ONLY GarmanKlassVolatility differs, and only in the
    // variants that pass a Period (period7, period7+unknown, json9): it used to ignore the Period and give the one-bar value (0.0179... / 0.0184... are the new 7- and 9-bar
    // values, the one-bar value 0.01725... stays in the variants without a Period). The new CoreGarmanKlassVolatilityParameter carries Period, so the screener Period now
    // reaches it. ExplicitPeriod_ReachesGarmanKlass_LikeAStandaloneCalculation pins the new values against a standalone calculation.
    // THIRD re-pin, 2026-11-14 (WebAiExpansion plan 03 T9; additive, OWNER-APPROVED 2026-11-14, answer M-3 = A): seven new indicator types (ChandelierExit, T3, McGinleyDynamic,
    // TSI, StochRSI, KVO, VolumeProfileLevels) add their 35 lines (5 variants each) to the report, so the hash over EVERY type changes: A5D5430A...C7FE -> A866361B...5718. NO existing line changed:
    // the report without the lines of the seven types still hashes to the previous pin A5D5430A...C7FE, which ValuesOfTheTypesBeforePlanThreeAreUnchanged keeps pinned.
    //
    // STANDING RULE for a re-pin that only ADDS indicator types (owner decision 2026-11-14; also in docs/ja/memo/lessons.md, lessons_webai_expansion_20261114_0001). No individual approval is needed when ALL hold:
    //   1. The report without the lines of every type that existed just before the addition (this plan's seven types included) hashes to the whole-report pin of that moment.
    //   2. The only added lines are those of the newly added types, and each value equals a standalone calculation (recorded in the step log).
    //   3. This header records the date, the added types and the old -> new hash.
    // A re-pin that changes any line of an existing type (the two earlier re-pins above) still needs the owner's approval one by one.
    // Backlog: a per-type hash structure that needs no re-pin at all is registered in docs/ja/Future_Improvements.md (2026-11-14, ScreenerDefaultParameterCacheParityTests per-type fingerprint).
    private const string PinnedFingerprint = "A866361BC174797D09B617299A3FCC9E02AEAF1E2BAFC15AE64E13F4BB635718";

    /// <summary>The fingerprint of the report without the lines of the types added by plan 03 (the pin before them).</summary>
    private const string FingerprintBeforePlanThree = "A5D5430A03AB40446FE8EB92DAA52262574C3F8A5D49B73F14B3BA4DCA14C7FE";

    private static readonly string[] PlanThreeTypes = { "ChandelierExit", "T3", "McGinleyDynamic", "TSI", "StochRSI", "KVO", "VolumeProfileLevels" };

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
    public void ValuesOfTheTypesBeforePlanThreeAreUnchanged()
    {
        var kept = BuildReport().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !PlanThreeTypes.Contains(line.Split('|')[0]));
        string report = string.Join("\n", kept) + "\n";

        Assert.Equal(FingerprintBeforePlanThree, Fingerprint(report));
    }

    [Theory]
    [InlineData(IndicatorType.BandWidth, 7)]
    [InlineData(IndicatorType.BandWidth, 9)]
    [InlineData(IndicatorType.BollingerBandsRatio, 7)]
    [InlineData(IndicatorType.BollingerBandsRatio, 9)]
    [InlineData(IndicatorType.VaR, 7)]
    [InlineData(IndicatorType.VaR, 9)]
    public void ExplicitPeriod_ReachesBandWidthBollingerBandsRatioAndVaR_LikeAStandaloneCalculation(IndicatorType type, int period)
    {
        var candles = Candles(300);
        var extractor = new ScreenerValueExtractor();
        var config = new ScreenerIndicatorSideConfig { IndicatorType = type, Parameters = new Dictionary<string, object> { ["Period"] = period } };

        decimal? screener = extractor.ExtractValueNullable(config, candles);

        decimal? standalone = type switch
        {
            IndicatorType.BandWidth => new StockAnalyzer.Core.Models.Indicators.Oscillators.CoreBandWidthIndicator { Period = period }.Calculate(candles).MainValues[^1],
            IndicatorType.BollingerBandsRatio => new StockAnalyzer.Core.Models.Indicators.Oscillators.CoreBollingerBandsRatioIndicator { Period = period }.Calculate(candles).MainValues[^1],
            _ => new StockAnalyzer.Core.Models.Indicators.Volatility.CoreVarIndicator { Period = period }.Calculate(candles).MainValues[^1],
        };

        Assert.NotNull(standalone);
        Assert.Equal(standalone, screener);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void ExplicitPeriod_ReachesGarmanKlass_LikeAStandaloneCalculation(int period)
    {
        var candles = Candles(300);
        var extractor = new ScreenerValueExtractor();
        var config = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.GarmanKlassVolatility, Parameters = new Dictionary<string, object> { ["Period"] = period } };

        decimal? screener = extractor.ExtractValueNullable(config, candles);
        decimal? standalone = new StockAnalyzer.Core.Models.Indicators.Volatility.CoreGarmanKlassVolatilityIndicator { Period = period }.Calculate(candles).MainValues[^1];
        decimal? oneBar = new StockAnalyzer.Core.Models.Indicators.Volatility.CoreGarmanKlassVolatilityIndicator().Calculate(candles).MainValues[^1];

        Assert.NotNull(standalone);
        Assert.Equal(standalone, screener);
        Assert.NotEqual(oneBar, screener);

        // Without a Period the screener still gives the one-bar value.
        Assert.Equal(oneBar, extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.GarmanKlassVolatility, Parameters = new Dictionary<string, object>() }, candles));
    }

    [Fact]
    public void TheReportIsDeterministic()
    {
        Assert.Equal(Fingerprint(BuildReport()), Fingerprint(BuildReport()));
    }
}
