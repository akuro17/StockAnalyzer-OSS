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
    // FOURTH re-pin, 2026-11-14 (WebAiExpansion plan 04, Run 4; NOT purely additive, OWNER-APPROVED one by one 2026-11-14, answer Q2' = A): A866361B...5718 -> 26491E7E...8511.
    //   Added types: ParkinsonVolatility and RogersSatchellVolatility (10 lines, 5 variants each).
    //   Changed lines of EXISTING types: the 15 lines of Garch11, HurstExponent and Entropy (3 types x 5 variants). They were enum members since before plan 03, with a placeholder class that
    //   returned only nulls, so their lines were part of every earlier pin as "<type>|<variant>|null"; plan 04 implemented them and the lines now carry the values. NO other line changed.
    //   This is why the standing rule's condition 1 cannot be met by REMOVING those types: a type that already exists as an enum member is not "added" (docs/ja/memo/lessons.md,
    //   lessons_webai_expansion_20261114_0005). The proof is kept permanently instead: with the two new types removed and the lines of the three former placeholders put back to
    //   their old "null", the report hashes to the plan-03 pin A866361B...5718 (ValuesOfTheTypesBeforePlanFourAreUnchanged) and, with the seven plan-03 types removed as well, to the pin
    //   before plan 03, A5D5430A...C7FE (ValuesOfTheTypesBeforePlanThreeAreUnchanged); so every type that existed before plan 04, the seven plan-03 types included, still gives its old values.
    //   Every value of the five plan-04 types equals a standalone calculation (ThePlanFourTypesGiveTheValueOfAStandaloneCalculation_WithoutAndWithAPeriod).
    // SIXTH re-pin, 2026-11-15 (WebAiExpansion plan 10 T2+T3; purely additive under the standing rule of lessons_webai_expansion_20261114_0001):
    //   Added type: MarketBreadth (5 lines, 5 variants, all "null" without injected universe breadth data); existing McClellanOscillator remains "null" without injected universe breadth data (1015 -> 1020 lines).
    //   Old -> new hash: 7EB7D9F2C29CF6F8ED0B9418FD400E0EF9EE0511553CE42E18FD0853B8F20D93 -> C8104FB99BFE0D1A68A9E507D2DD3CE6482A13DA5E26BA1D98A8BDA20C04AD4A.
    //   No existing line changed: removing MarketBreadth returns the plan-09 completion pin (ValuesOfTheTypesBeforePlanTenAreUnchanged).
    // SEVENTH re-pin, 2026-11-16 (TimeSeries Plan 02 Task 2B; purely additive under the standing rule of lessons_webai_expansion_20261114_0001):
    //   Added type: HarRv (5 lines, 5 variants; 1020 -> 1025 lines).
    //   Old -> new hash: C8104FB99BFE0D1A68A9E507D2DD3CE6482A13DA5E26BA1D98A8BDA20C04AD4A -> 8FE6FA0D108FC47C6E661BF2E202687B11ECD97AA7AF0C81CC971BCD2888AB94.
    //   No existing line changed: removing HarRv returns the prior pin (ValuesOfTheTypesBeforeTimeSeriesPlanTwoAreUnchanged).
    // EIGHTH re-pin, 2026-11-16 (TimeSeries Plan 04 Task 4B; purely additive under the standing rule of lessons_webai_expansion_20261114_0001):
    //   Added type: Bocpd (5 lines, 5 variants; 1025 -> 1030 lines).
    //   Old -> new hash: 8FE6FA0D108FC47C6E661BF2E202687B11ECD97AA7AF0C81CC971BCD2888AB94 -> A0E4AD1FA3011A952DF2E2840B0A1B91D431F14B4E8E5ED0407CF040866CFBB8.
    //   No existing line changed: removing Bocpd returns the prior pin (ValuesOfTheTypesBeforeTimeSeriesPlanFourAreUnchanged).
    // NINTH re-pin, 2026-11-16 (TimeSeries Plan 05 Task 5A; purely additive under the standing rule of lessons_webai_expansion_20261114_0001):
    //   Added types: GrangerCausality, JohansenCointegration (10 lines, 5 variants each; 1030 -> 1040 lines).
    //   Old -> new hash: A0E4AD1FA3011A952DF2E2840B0A1B91D431F14B4E8E5ED0407CF040866CFBB8 -> 87630CE879B033AAA36432A6938231A4AAABB436F94832149DA13564DF9A5F88.
    //   No existing line changed: removing both returns the prior pin (ValuesOfTheTypesBeforeTimeSeriesPlanFiveAreUnchanged).
    // TENTH re-pin, 2026-11-16 (WebAi Audit Plan 01 Task 1.2; NOT purely additive, OWNER-APPROVED 2026-11-16):
    //   Changed type: HarRv (values updated by window mean-variance normalization before Ridge OLS in HarRvMath.cs; line count stays 1040).
    //   Old -> new hash: 87630CE879B033AAA36432A6938231A4AAABB436F94832149DA13564DF9A5F88 -> 9D0DE11C214D6F1B20113DE372E0F6DB48845B487FA0966D85A9F6E3CBBD7A84.
    //   Prior-type invariant preserved: ValuesOfTheTypesBeforeTimeSeriesPlanTwoAreUnchanged remains C8104FB99BFE0D1A68A9E507D2DD3CE6482A13DA5E26BA1D98A8BDA20C04AD4A.
    // ELEVENTH re-pin, 2026-11-17 (SSA auto-rank fix M4; NOT purely additive, OWNER-APPROVED 2026-11-17, decision R-1 = A, lessons_webai_expansion_20261114_0001):
    //   Changed type: SSAAnomaly (5 lines, one per variant; the line count stays 1040). CoreSSAAnomalyIndicator selects the rank with autoRank on (default true) and used to
    //   estimate it from the RAW Jacobi eigenvalue order; SsaRankSelector.EstimateSignalRank requires descending values, the indicator now reads them in descending order.
    //   Old -> new value of every SSAAnomaly line: -1.43361075590879 -> -0.523394926009718.
    //   Old -> new hash: 9D0DE11C214D6F1B20113DE372E0F6DB48845B487FA0966D85A9F6E3CBBD7A84 -> 759BAFA814FD8FCEAE5D9BF792EEE76864C6D6A7C144FEC25C2FDB4C3C43F331.
    //   Reconstruction proof: with the three-file fix reverted the report hashed exactly to the old pin (9D0DE11C...), and the diff between the two reports is those five lines only.
    //   Every earlier pin stays reproducible: the permanent proofs put the SSAAnomaly lines back to the old value (ChangedLineOldValues) before hashing.
    private const string PinnedFingerprint = "759BAFA814FD8FCEAE5D9BF792EEE76864C6D6A7C144FEC25C2FDB4C3C43F331";
    private const string FingerprintBeforeTimeSeriesPlanFive = "E5061217FAE4139B5786254FC783E046C360C30EAB9F3BE307E9E3DE2B3F769D";
    private const string FingerprintBeforeTimeSeriesPlanFour = "CD0FC956A95EB10B0470809C6C892B817CED627856044EEE84045FF6745380C0";
    private const string FingerprintBeforeTimeSeriesPlanTwo = "C8104FB99BFE0D1A68A9E507D2DD3CE6482A13DA5E26BA1D98A8BDA20C04AD4A";
    private const string FingerprintBeforePlanTen = "7EB7D9F2C29CF6F8ED0B9418FD400E0EF9EE0511553CE42E18FD0853B8F20D93";

    /// <summary>The fingerprint of the report before plan 09 (the pin at plan 04 completion), reproduced by <see cref="HashWithoutRemovedTypes"/>.</summary>
    private const string FingerprintBeforePlanNine = "26491E7E99D7935E28E356601D3ECC3D34533A75F3299C0ECC66C18F00028511";

    /// <summary>The fingerprint of the report before plan 04 (the pin at plan 03 completion), reproduced by <see cref="HashWithoutNewTypes"/>.</summary>
    private const string FingerprintBeforePlanFour = "A866361BC174797D09B617299A3FCC9E02AEAF1E2BAFC15AE64E13F4BB635718";

    /// <summary>The fingerprint of the report without the lines of the types added by plan 03 (the pin before them).</summary>
    private const string FingerprintBeforePlanThree = "A5D5430A03AB40446FE8EB92DAA52262574C3F8A5D49B73F14B3BA4DCA14C7FE";

    private static readonly string[] PlanThreeTypes = { "ChandelierExit", "T3", "McGinleyDynamic", "TSI", "StochRSI", "KVO", "VolumeProfileLevels" };

    /// <summary>The types plan 04 added to the enum (their lines did not exist before).</summary>
    private static readonly string[] PlanFourNewTypes = { "ParkinsonVolatility", "RogersSatchellVolatility" };

    /// <summary>The type plan 09 T1 added to the enum.</summary>
    private static readonly string[] PlanNineTypes = { "MansfieldRS" };

    /// <summary>The type plan 10 T3 added to the enum.</summary>
    private static readonly string[] PlanTenTypes = { "MarketBreadth" };

    /// <summary>The type TimeSeries Plan 02 Task 2B added to the enum.</summary>
    private static readonly string[] TimeSeriesPlanTwoTypes = { "HarRv" };

    /// <summary>The type TimeSeries Plan 04 Task 4B added to the enum.</summary>
    private static readonly string[] TimeSeriesPlanFourTypes = { "Bocpd" };

    /// <summary>The types TimeSeries Plan 05 Task 5A added to the enum.</summary>
    private static readonly string[] TimeSeriesPlanFiveTypes = { "GrangerCausality", "JohansenCointegration" };

    /// <summary>The enum members that existed before plan 03 with a placeholder class (always null) and were implemented by plan 04; only their VALUES changed.</summary>
    private static readonly string[] FormerPlaceholderTypes = { "Garch11", "HurstExponent", "Entropy" };

    /// <summary>The value text of every line of a type whose VALUES changed after the earlier pins were taken (eleventh re-pin: SSAAnomaly, one value for all variants).</summary>
    private static readonly Dictionary<string, string> ChangedLineOldValues = new() { ["SSAAnomaly"] = "-1.43361075590879" };

    /// <summary>The value text of a former placeholder in every pin before plan 04 (what <c>BuildReport</c> wrote for a calculation without a result).</summary>
    private const string FormerPlaceholderValue = NoValueText;

    /// <summary>What <see cref="BuildReport"/> writes for a calculation without a value (and wrote for the placeholders in every pin before plan 04).</summary>
    private const string NoValueText = "null";

    /// <summary>
    /// The report as it was before the listed types existed: the lines of <paramref name="removedTypes"/> are dropped and the lines of the former placeholders are put back to their
    /// old value. Every other line is the current one, so the hash returns to an old pin only if no line of any other type changed.
    /// </summary>
    private static string HashWithoutNewTypes(params string[][] removedTypes)
        => HashWithoutRemovedTypes(resetFormerPlaceholders: true, removedTypes);

    private static string HashWithoutRemovedTypes(bool resetFormerPlaceholders, params string[][] removedTypes)
    {
        var removed = removedTypes.SelectMany(types => types).ToHashSet();
        var lines = BuildReport().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !removed.Contains(line.Split('|')[0]))
            .Select(line =>
            {
                string[] parts = line.Split('|');
                if (ChangedLineOldValues.TryGetValue(parts[0], out string? oldValue)) return $"{parts[0]}|{parts[1]}|{oldValue}";
                if (!resetFormerPlaceholders) return line;
                return FormerPlaceholderTypes.Contains(parts[0]) ? $"{parts[0]}|{parts[1]}|{FormerPlaceholderValue}" : line;
            });
        return Fingerprint(string.Join("\n", lines) + "\n");
    }
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
                    text = value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : NoValueText;
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
        // Without the seven plan-03 types, the two plan-04 types, MansfieldRS, MarketBreadth, HarRv, Bocpd, GrangerCausality, and JohansenCointegration, and with the three former placeholders as they were: the pin before plan 03.
        Assert.Equal(FingerprintBeforePlanThree, HashWithoutNewTypes(PlanThreeTypes, PlanFourNewTypes, PlanNineTypes, PlanTenTypes, TimeSeriesPlanTwoTypes, TimeSeriesPlanFourTypes, TimeSeriesPlanFiveTypes));
    }

    [Fact]
    public void ValuesOfTheTypesBeforePlanFourAreUnchanged_ThePlanThreeTypesIncluded()
    {
        // Without the two plan-04 types, MansfieldRS, MarketBreadth, HarRv, Bocpd, GrangerCausality, and JohansenCointegration (the seven plan-03 types stay in the check) and with the three former placeholders put back to null:
        // the pin at plan 03 completion. Anything else that moved while plan 04, plan 09, plan 10, TimeSeries Plan 02, TimeSeries Plan 04, or TimeSeries Plan 05 was built makes this fail.
        Assert.Equal(FingerprintBeforePlanFour, HashWithoutNewTypes(PlanFourNewTypes, PlanNineTypes, PlanTenTypes, TimeSeriesPlanTwoTypes, TimeSeriesPlanFourTypes, TimeSeriesPlanFiveTypes));
    }

    [Fact]
    public void ValuesOfTheTypesBeforePlanNineAreUnchanged()
    {
        // MansfieldRS, MarketBreadth, HarRv, Bocpd, GrangerCausality, and JohansenCointegration are removed (all plan-03 and plan-04 types stay as implemented): the pin at plan 04 completion.
        Assert.Equal(FingerprintBeforePlanNine, HashWithoutRemovedTypes(resetFormerPlaceholders: false, PlanNineTypes, PlanTenTypes, TimeSeriesPlanTwoTypes, TimeSeriesPlanFourTypes, TimeSeriesPlanFiveTypes));
    }

    [Fact]
    public void ValuesOfTheTypesBeforePlanTenAreUnchanged()
    {
        // MarketBreadth, HarRv, Bocpd, GrangerCausality, and JohansenCointegration are removed (all plan-03, plan-04, and plan-09 types stay as implemented): the pin at plan 09 completion.
        Assert.Equal(FingerprintBeforePlanTen, HashWithoutRemovedTypes(resetFormerPlaceholders: false, PlanTenTypes, TimeSeriesPlanTwoTypes, TimeSeriesPlanFourTypes, TimeSeriesPlanFiveTypes));
    }

    [Fact]
    public void ValuesOfTheTypesBeforeTimeSeriesPlanTwoAreUnchanged()
    {
        // HarRv, Bocpd, GrangerCausality, and JohansenCointegration are removed: returns the prior whole-report pin before HarRv identically.
        Assert.Equal(FingerprintBeforeTimeSeriesPlanTwo, HashWithoutRemovedTypes(resetFormerPlaceholders: false, TimeSeriesPlanTwoTypes, TimeSeriesPlanFourTypes, TimeSeriesPlanFiveTypes));
    }

    [Fact]
    public void ValuesOfTheTypesBeforeTimeSeriesPlanFourAreUnchanged()
    {
        // Bocpd, GrangerCausality, and JohansenCointegration are removed: returns the prior whole-report pin identically.
        string actual = HashWithoutRemovedTypes(resetFormerPlaceholders: false, TimeSeriesPlanFourTypes, TimeSeriesPlanFiveTypes);
        _output.WriteLine("FINGERPRINT_BEFORE_TS_PLAN4 " + actual);
        Assert.Equal(FingerprintBeforeTimeSeriesPlanFour, actual);
    }

    [Fact]
    public void ValuesOfTheTypesBeforeTimeSeriesPlanFiveAreUnchanged()
    {
        // Only GrangerCausality and JohansenCointegration are removed: returns the prior whole-report pin identically.
        string actual = HashWithoutRemovedTypes(resetFormerPlaceholders: false, TimeSeriesPlanFiveTypes);
        _output.WriteLine("FINGERPRINT_BEFORE_TS_PLAN5 " + actual);
        Assert.Equal(FingerprintBeforeTimeSeriesPlanFive, actual);
    }

    [Fact]
    public void MansfieldRSWithoutComparisonSymbol_MatchesStandaloneNullOnAllVariants()
    {
        var candles = Candles(300);
        var extractor = new ScreenerValueExtractor();
        var nine = JsonDocument.Parse("9").RootElement;
        var variants = new[]
        {
            new Dictionary<string, object> { ["Period"] = 7 },
            new Dictionary<string, object>(),
            new Dictionary<string, object> { ["Period"] = 7, ["NoSuchKey"] = 1 },
            new Dictionary<string, object> { ["Period"] = nine },
            new Dictionary<string, object>(),
        };

        foreach (var parameters in variants)
        {
            decimal? screener = extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.MansfieldRS, Parameters = parameters }, candles);
            decimal? standalone = new StockAnalyzer.Core.Models.Indicators.Statistics.CoreMansfieldRSIndicator().Calculate(candles).MainValues[^1];
            Assert.Null(screener);
            Assert.Equal(standalone, screener);
        }
    }

    // Plan 04 re-pin condition 2: every value of a plan-04 line equals a standalone calculation of the same indicator. A Period outside the range of the indicator's parameter
    // object makes its Configure throw; the screener then keeps the defaults (ScreenerValueExtractor, "best effort parameter configuration fallback"), so the standalone
    // calculation uses the default Period in that case.
    private static int PeriodOrDefault(int? period, int min, int max, int defaultPeriod) => period.HasValue && period.Value >= min && period.Value <= max ? period.Value : defaultPeriod;

    private static decimal? StandaloneLastValue(IndicatorType type, int? period, IReadOnlyList<CoreCandleData> candles) => type switch
    {
        IndicatorType.ParkinsonVolatility => new StockAnalyzer.Core.Models.Indicators.Volatility.CoreParkinsonVolatilityIndicator
        {
            Period = PeriodOrDefault(period, IndicatorDefaultConstants.RangeVolatilityMinPeriod, IndicatorDefaultConstants.RangeVolatilityMaxPeriod, IndicatorDefaultConstants.RangeVolatilityDefaultPeriod)
        }.Calculate(candles).MainValues[^1],
        IndicatorType.RogersSatchellVolatility => new StockAnalyzer.Core.Models.Indicators.Volatility.CoreRogersSatchellVolatilityIndicator
        {
            Period = PeriodOrDefault(period, IndicatorDefaultConstants.RangeVolatilityMinPeriod, IndicatorDefaultConstants.RangeVolatilityMaxPeriod, IndicatorDefaultConstants.RangeVolatilityDefaultPeriod)
        }.Calculate(candles).MainValues[^1],
        IndicatorType.Garch11 => new StockAnalyzer.Core.Models.Indicators.Advanced.CoreGarch11Indicator
        {
            Period = PeriodOrDefault(period, IndicatorDefaultConstants.Garch11MinPeriod, IndicatorDefaultConstants.Garch11MaxPeriod, IndicatorDefaultConstants.Garch11DefaultPeriod)
        }.Calculate(candles).MainValues[^1],
        IndicatorType.HurstExponent => new StockAnalyzer.Core.Models.Indicators.Advanced.CoreHurstExponentIndicator
        {
            Period = PeriodOrDefault(period, IndicatorDefaultConstants.HurstMinPeriod, IndicatorDefaultConstants.HurstMaxPeriod, IndicatorDefaultConstants.HurstDefaultPeriod)
        }.Calculate(candles).MainValues[^1],
        IndicatorType.Entropy => new StockAnalyzer.Core.Models.Indicators.Advanced.CoreEntropyIndicator
        {
            Period = PeriodOrDefault(period, IndicatorDefaultConstants.EntropyMinPeriod, IndicatorDefaultConstants.EntropyMaxPeriod, IndicatorDefaultConstants.EntropyDefaultPeriod)
        }.Calculate(candles).MainValues[^1],
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [Theory]
    [InlineData(IndicatorType.ParkinsonVolatility)]
    [InlineData(IndicatorType.RogersSatchellVolatility)]
    [InlineData(IndicatorType.Garch11)]
    [InlineData(IndicatorType.HurstExponent)]
    [InlineData(IndicatorType.Entropy)]
    public void ThePlanFourTypesGiveTheValueOfAStandaloneCalculation_WithoutAndWithAPeriod(IndicatorType type)
    {
        var candles = Candles(300);
        var extractor = new ScreenerValueExtractor();

        foreach (int? period in new int?[] { null, 7, 9 })
        {
            var parameters = period.HasValue ? new Dictionary<string, object> { ["Period"] = period.Value } : new Dictionary<string, object>();
            decimal? screener = extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = type, Parameters = parameters }, candles);

            decimal? standalone = StandaloneLastValue(type, period, candles);

            Assert.NotNull(standalone);
            Assert.Equal(standalone, screener);
        }
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
