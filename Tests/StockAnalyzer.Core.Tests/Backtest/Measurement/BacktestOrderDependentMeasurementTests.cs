#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using StockAnalyzer.Core.Tests.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Measurement;

/// <summary>
/// Measurement harness for order-dependent bars (owner decision H1 = 1 of 2026-10-16; plan docs/sa_implementation_plan_BacktestOrderDependentMeasurement).
/// It answers one question for the owner's G1 / H2 decision (build <c>IntrabarPathMode.AdversePath</c>?): how often is a bar order-dependent on real daily data, and
/// how much do the trade count, the total result and the maximum drawdown move when the intra-bar convention changes?
/// Opt-in only (<c>SA_BACKTEST_MEASURE</c>); it reads stored daily parquet files read-only and writes CSV and a Markdown report to <c>SA_BACKTEST_MEASURE_OUT</c>.
/// Candles are read here with a side-effect-free DuckDB query instead of <c>ParquetMarketDataProvider</c>, whose constructor creates data folders and a ticker list. The query and the
/// null-fill rules (open = close, high = max(open, close), low = min(open, close), volume = 0) are copied from <c>ParquetMarketDataProvider.GetTickersDataAsync</c>, and a row that is not a
/// valid candle is dropped; keep them in step with it.
/// The strategy presets and account settings are illustrative (a trend rule with entry-relative Stop-Loss / Take-Profit), NOT the owner's strategies: the numbers say how
/// sensitive a backtest of this kind is, not how any particular strategy performs. Nothing here changes engine behavior.
/// </summary>
public sealed class BacktestOrderDependentMeasurementTests
{
    public const string EnabledVariable = "SA_BACKTEST_MEASURE";
    public const string DataVariable = "SA_BACKTEST_MEASURE_DATA";
    public const string OutputVariable = "SA_BACKTEST_MEASURE_OUT";
    public const string TickersVariable = "SA_BACKTEST_MEASURE_TICKERS";

    /// <summary>Every tunable of the measurement in one place; it is written into the report so a result can be reproduced.</summary>
    private static class Policy
    {
        public const int SmaPeriod = 50;
        public const int MinimumBars = 300;
        public const int GroupCount = 3;
        public const decimal UpYearReturn = 0.10m;
        public const decimal DownYearReturn = -0.10m;
        public const decimal ShockDailyMove = 0.08m;
        public const string NoVolumeGroup = "NoVolume";
        public const string JapaneseSuffix = "-T";

        public static readonly (string Name, decimal StopLoss, decimal TakeProfit)[] RiskPresets =
        {
            ("SL3-TP3", 0.03m, 0.03m),
            ("SL5-TP10", 0.05m, 0.10m),
            ("SL10-TP20", 0.10m, 0.20m),
        };

        /// <summary>A stress setting outside the application defaults (up to 10x exposure): it shows whether a margin liquidation can race a pending exit order at all.</summary>
        public const decimal HighLeverageInitialMargin = 0.10m;
        public const decimal HighLeverageMaintenanceMargin = 0.05m;

        public const string AccountAppDefault = "AppDefault";
        public const string AccountFullyInvested = "FullyInvested";
        public const string AccountHighLeverage = "HighLeverage";
        public const string VariantBase = "Base";
        public const string VariantConflictAlt = "ConflictAlt";
        public const string VariantPathAlt = "PathAlt";

        public static readonly string[] Accounts = { AccountAppDefault, AccountFullyInvested, AccountHighLeverage };
        public static readonly string[] Variants = { VariantBase, VariantConflictAlt, VariantPathAlt };
    }

    private sealed record Series(string Symbol, ImmutableArray<CandleData> Bars, double Volatility, double MedianDollarVolume, string Market);

    private sealed record Run(
        string Symbol, string Risk, string Account, string Variant, int Bars, int Trades, decimal FinalEquity, decimal InitialCapital, double MaxDrawdown,
        int Detected, int Affecting, int RiskTies, int ExitVersusLiquidation, string Status, string Hash,
        ImmutableArray<EquityPoint> Equity, ImmutableArray<BacktestWarning> Warnings);

    private sealed record Group(string VolatilityGroup, string LiquidityGroup);

    [EnvironmentFact(EnabledVariable)]
    public void MeasureOrderDependentBars()
    {
        string dataDirectory = Required(DataVariable, "the folder with the stored daily parquet files (for example Data/Daily)");
        string outputDirectory = Required(OutputVariable, "the folder that receives the CSV files and the report (under Y:\\Temp)");
        Directory.CreateDirectory(outputDirectory);
        HashSet<string>? requested = Environment.GetEnvironmentVariable(TickersVariable) is { Length: > 0 } list
            ? list.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var skipped = new List<string>();
        List<Series> series = LoadSeries(dataDirectory, requested, skipped);
        Assert.True(series.Count > 0, "No usable series. First skips: " + string.Join(" | ", skipped.Take(3)));
        Dictionary<string, Group> groups = BuildGroups(series);

        var runs = new List<Run>();
        BacktestEngine engine = VerificationHarness.CreateEngine();
        foreach (Series s in series)
        {
            BacktestInput input = ToInput(s);
            foreach ((string riskName, decimal stopLoss, decimal takeProfit) in Policy.RiskPresets)
            {
                foreach (string account in Policy.Accounts)
                {
                    foreach (string variant in Policy.Variants)
                    {
                        try
                        {
                            runs.Add(Execute(engine, input, s.Symbol, riskName, stopLoss, takeProfit, account, variant));
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            skipped.Add($"{s.Symbol} {riskName} {account} {variant}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
        }

        Assert.True(runs.Count > 0, "No run succeeded. First failures: " + string.Join(" | ", skipped.Take(3)));
        AssertReproducible(engine, series[0], runs);

        List<Pair> pairs = BuildPairs(runs, groups);
        List<RegimeRow> regimes = BuildRegimes(runs, series);
        WriteCsv(Path.Combine(outputDirectory, "runs.csv"), runs);
        WritePairsCsv(Path.Combine(outputDirectory, "pairs.csv"), pairs);
        WriteRegimesCsv(Path.Combine(outputDirectory, "regimes.csv"), regimes);
        WriteTextAtomically(Path.Combine(outputDirectory, "report.md"), BuildReport(dataDirectory, series, groups, runs, pairs, regimes, skipped));
    }

    private static string Required(string name, string meaning)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} names {meaning}.");

    // ------------------------------------------------------------------ data

    private static List<Series> LoadSeries(string directory, HashSet<string>? requested, List<string> skipped)
    {
        var result = new List<Series>();
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        foreach (string file in Directory.EnumerateFiles(directory, "*.parquet").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".meta", StringComparison.Ordinal) || name.EndsWith(".signals", StringComparison.Ordinal)) continue;
            if (requested is not null && !requested.Contains(name)) continue;

            ImmutableArray<CandleData> bars;
            try
            {
                bars = ReadBars(connection, file);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                skipped.Add($"{name}: unreadable ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            if (bars.Length < Policy.MinimumBars)
            {
                skipped.Add($"{name}: {bars.Length} bars (< {Policy.MinimumBars})");
                continue;
            }

            var returns = new List<double>(bars.Length - 1);
            for (int i = 1; i < bars.Length; i++) returns.Add((double)(bars[i].Close / bars[i - 1].Close - 1m));
            double mean = returns.Average();
            double volatility = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / returns.Count);
            double[] dollarVolume = bars.Select(b => (double)b.Close * b.Volume).OrderBy(v => v).ToArray();
            double median = dollarVolume[dollarVolume.Length / 2];
            string market = median <= 0d ? Policy.NoVolumeGroup : name.EndsWith(Policy.JapaneseSuffix, StringComparison.Ordinal) ? "JP" : "Other";
            result.Add(new Series(name, bars, volatility, median, market));
        }

        return result;
    }

    private static ImmutableArray<CandleData> ReadBars(DuckDBConnection connection, string file)
    {
        using var command = connection.CreateCommand();
        string escaped = file.Replace("'", "''", StringComparison.Ordinal);
        command.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{escaped}') WHERE close IS NOT NULL AND date IS NOT NULL ORDER BY date ASC";
        using var reader = command.ExecuteReader();
        var bars = ImmutableArray.CreateBuilder<CandleData>();
        DateTime? previous = null;
        while (reader.Read())
        {
            DateTime date = DateTime.SpecifyKind(reader.GetDateTime(0).Date, DateTimeKind.Utc);
            if (previous is { } p && date <= p) continue;
            decimal close = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
            decimal open = reader.IsDBNull(1) ? close : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            decimal high = reader.IsDBNull(2) ? Math.Max(open, close) : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            decimal low = reader.IsDBNull(3) ? Math.Min(open, close) : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
            long volume = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
            // The engine input requires a valid candle; a malformed row is dropped (and the file keeps its remaining bars) rather than repaired.
            if (open <= 0m || high <= 0m || low <= 0m || close <= 0m || low > Math.Min(open, close) || high < Math.Max(open, close)) continue;
            bars.Add(new CandleData(date, open, high, low, close, volume));
            previous = date;
        }

        return bars.ToImmutable();
    }

    private static BacktestInput ToInput(Series s)
        => new(s.Bars, s.Symbol, TimeFrame.D1, BacktestInput.CurrentDataVersion, s.Bars[0].Timestamp, s.Bars[^1].Timestamp.AddDays(1), 0, 0);

    private static Dictionary<string, Group> BuildGroups(List<Series> series)
    {
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (IGrouping<string, Series> market in series.GroupBy(s => s.Market))
        {
            Dictionary<string, string> volatility = Terciles(market.OrderBy(s => s.Volatility).Select(s => s.Symbol).ToList(), "Vol");
            Dictionary<string, string> liquidity = market.Key == Policy.NoVolumeGroup
                ? market.ToDictionary(s => s.Symbol, _ => "Liq-n/a", StringComparer.Ordinal)
                : Terciles(market.OrderBy(s => s.MedianDollarVolume).Select(s => s.Symbol).ToList(), "Liq");
            foreach (Series s in market) groups[s.Symbol] = new Group($"{market.Key}:{volatility[s.Symbol]}", $"{market.Key}:{liquidity[s.Symbol]}");
        }

        return groups;
    }

    private static Dictionary<string, string> Terciles(List<string> ascending, string prefix)
    {
        string[] names = { "Low", "Mid", "High" };
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < ascending.Count; i++) map[ascending[i]] = $"{prefix}-{names[Math.Min(Policy.GroupCount - 1, i * Policy.GroupCount / ascending.Count)]}";
        return map;
    }

    // ------------------------------------------------------------------ runs

    private static BacktestConfiguration MakeConfig(string account, string variant) => new()
    {
        // The application defaults (BacktestDefaults) for everything the measurement does not vary.
        InitialCapital = BacktestDefaults.InitialCapital,
        CommissionFlat = BacktestDefaults.CommissionFlat,
        CommissionPerUnit = BacktestDefaults.CommissionPerUnit,
        SlippageRatio = BacktestDefaults.SlippageRatio,
        InitialMarginRatio = account == Policy.AccountHighLeverage ? Policy.HighLeverageInitialMargin : BacktestDefaults.InitialMarginRatio,
        MaintenanceMarginRatio = account == Policy.AccountHighLeverage ? Policy.HighLeverageMaintenanceMargin : BacktestDefaults.MaintenanceMarginRatio,
        LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio,
        SizingModel = account == Policy.AccountAppDefault ? BacktestDefaults.SizingModel : PositionSizingModel.PercentOfEquity,
        SizingParameter = account == Policy.AccountAppDefault ? BacktestDefaults.SizingParameter : 1m,
        PricePathConflictMode = variant == Policy.VariantConflictAlt ? PricePathConflictMode.CanonicalOHLC : PricePathConflictMode.Conservative,
        IntrabarPathMode = variant == Policy.VariantPathAlt ? IntrabarPathMode.CanonicalOHLC : IntrabarPathMode.DeterministicCandleShape,
    };

    private static ConditionBasedBacktestStrategy MakeStrategy(decimal stopLoss, decimal takeProfit)
    {
        BacktestConditionSide Close() => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close };
        BacktestConditionSide Sma() => new() { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = Policy.SmaPeriod } };
        var entry = new BacktestConditionEntry
        {
            Left = Close(), Operator = ComparisonOperator.GreaterThan, TargetMode = RightHandTargetMode.Indicator, Right = Sma(),
            Role = BacktestConditionRole.EntryOnly, Position = TradeSide.Long,
        };
        var exit = new BacktestConditionEntry
        {
            Left = Close(), Operator = ComparisonOperator.LessThan, TargetMode = RightHandTargetMode.Indicator, Right = Sma(),
            Role = BacktestConditionRole.ExitOnly,
        };
        return new ConditionBasedBacktestStrategy(new[] { entry, exit }, new BacktestRiskManagementSettings { StopLossPercent = stopLoss, TakeProfitPercent = takeProfit });
    }

    private static Run Execute(BacktestEngine engine, BacktestInput input, string symbol, string risk, decimal stopLoss, decimal takeProfit, string account, string variant)
    {
        BacktestConfiguration config = MakeConfig(account, variant);
        BacktestResult result = engine.Run(input, config, MakeStrategy(stopLoss, takeProfit));
        OrderDependentBarCounts counts = OrderDependentBarCounts.Count(result.Warnings);
        double maxDrawdown = result.EquityPoints.IsEmpty
            ? 0d
            : DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(result.EquityPoints.Select(e => e.Equity).ToImmutableArray()).Max();
        return new Run(
            symbol, risk, account, variant, result.EquityPoints.Length, result.Trades.Length,
            result.EquityPoints.IsEmpty ? config.InitialCapital : result.EquityPoints[^1].Equity, config.InitialCapital, maxDrawdown,
            counts.Detected, counts.Affecting, counts.RiskTies, counts.ExitVersusLiquidation, result.Status.ToString(),
            Convert.ToHexString(result.ReproducibilityHash), result.EquityPoints, result.Warnings);
    }

    /// <summary>A measurement is only worth reading if a repeated run gives the same digest, warnings and counts.</summary>
    private static void AssertReproducible(BacktestEngine engine, Series first, List<Run> runs)
    {
        Run original = runs.First(r => r.Symbol == first.Symbol);
        (string name, decimal stopLoss, decimal takeProfit) = Policy.RiskPresets.First(p => p.Name == original.Risk);
        Run again = Execute(engine, ToInput(first), original.Symbol, name, stopLoss, takeProfit, original.Account, original.Variant);
        Assert.Equal(original.Hash, again.Hash);
        Assert.Equal(original.Warnings.ToArray(), again.Warnings.ToArray());
        Assert.Equal((original.Detected, original.Affecting, original.Trades), (again.Detected, again.Affecting, again.Trades));
    }

    // ------------------------------------------------------------------ comparison

    private sealed record Pair(
        Run Base, Run Alt, string VolatilityGroup, string LiquidityGroup,
        decimal DeltaReturn, decimal RelativeEquityChange, int DeltaTrades, double DeltaMaxDrawdown);

    private static List<Pair> BuildPairs(List<Run> runs, Dictionary<string, Group> groups)
    {
        var baseline = runs.Where(r => r.Variant == Policy.VariantBase).ToDictionary(r => (r.Symbol, r.Risk, r.Account));
        var pairs = new List<Pair>();
        foreach (Run alt in runs.Where(r => r.Variant != Policy.VariantBase))
        {
            if (!baseline.TryGetValue((alt.Symbol, alt.Risk, alt.Account), out Run? b)) continue;
            Group g = groups[alt.Symbol];
            pairs.Add(new Pair(b, alt, g.VolatilityGroup, g.LiquidityGroup,
                (alt.FinalEquity - b.FinalEquity) / b.InitialCapital,
                b.FinalEquity > 0m ? alt.FinalEquity / b.FinalEquity - 1m : 0m,
                alt.Trades - b.Trades, alt.MaxDrawdown - b.MaxDrawdown));
        }

        return pairs;
    }

    private sealed record RegimeRow(
        string Symbol, string Risk, string Account, string Variant, int Year, string Label, bool Shock, int Bars, int Detected, int Affecting, decimal DeltaReturn);

    private static List<RegimeRow> BuildRegimes(List<Run> runs, List<Series> series)
    {
        var yearInfo = new Dictionary<string, Dictionary<int, (string Label, bool Shock)>>(StringComparer.Ordinal);
        foreach (Series s in series)
        {
            var years = new Dictionary<int, (string, bool)>();
            foreach (IGrouping<int, int> year in Enumerable.Range(0, s.Bars.Length).GroupBy(i => s.Bars[i].Timestamp.Year))
            {
                int first = year.Min();
                int last = year.Max();
                decimal startPrice = first > 0 ? s.Bars[first - 1].Close : s.Bars[first].Open;
                decimal yearReturn = s.Bars[last].Close / startPrice - 1m;
                bool shock = year.Any(i => i > 0 && Math.Abs(s.Bars[i].Close / s.Bars[i - 1].Close - 1m) >= Policy.ShockDailyMove);
                string label = yearReturn >= Policy.UpYearReturn ? "Up" : yearReturn <= Policy.DownYearReturn ? "Down" : "Range";
                years[year.Key] = (label, shock);
            }

            yearInfo[s.Symbol] = years;
        }

        var baseline = runs.Where(r => r.Variant == Policy.VariantBase).ToDictionary(r => (r.Symbol, r.Risk, r.Account));
        var rows = new List<RegimeRow>();
        foreach (Run alt in runs.Where(r => r.Variant != Policy.VariantBase))
        {
            if (!baseline.TryGetValue((alt.Symbol, alt.Risk, alt.Account), out Run? b)) continue;
            Dictionary<int, decimal> baseEnd = YearEndEquity(b);
            Dictionary<int, decimal> altEnd = YearEndEquity(alt);
            decimal previousBase = b.InitialCapital;
            decimal previousAlt = alt.InitialCapital;
            foreach (KeyValuePair<int, (string Label, bool Shock)> year in yearInfo[alt.Symbol].OrderBy(y => y.Key))
            {
                decimal endBase = baseEnd.TryGetValue(year.Key, out decimal eb) ? eb : previousBase;
                decimal endAlt = altEnd.TryGetValue(year.Key, out decimal ea) ? ea : previousAlt;
                // Difference of the two yearly returns (variant minus base), each measured on its own start-of-year equity.
                decimal delta = previousBase > 0m && previousAlt > 0m ? (endAlt / previousAlt - 1m) - (endBase / previousBase - 1m) : 0m;
                int bars = b.Equity.Count(e => e.Timestamp.Year == year.Key);
                int detected = 0;
                int affecting = 0;
                foreach (BacktestWarning w in b.Warnings.Where(w => w.Timestamp.Year == year.Key))
                {
                    OrderDependentBarCounts one = OrderDependentBarCounts.Count(new[] { w });
                    detected += one.Detected;
                    affecting += one.Affecting;
                }

                rows.Add(new RegimeRow(alt.Symbol, alt.Risk, alt.Account, alt.Variant, year.Key, year.Value.Label, year.Value.Shock, bars, detected, affecting, delta));
                previousBase = endBase;
                previousAlt = endAlt;
            }
        }

        return rows;
    }

    private static Dictionary<int, decimal> YearEndEquity(Run run)
    {
        var map = new Dictionary<int, decimal>();
        foreach (EquityPoint point in run.Equity) map[point.Timestamp.Year] = point.Equity;
        return map;
    }

    // ------------------------------------------------------------------ output

    /// <summary>Writes through a temporary file and replaces the target in one move, so a reader never sees a half-written report.</summary>
    private static void WriteTextAtomically(string path, string text)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string F(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void WriteCsv(string path, List<Run> runs)
    {
        var sb = new StringBuilder("symbol,risk,account,variant,bars,trades,final_equity,max_drawdown,detected,affecting,risk_ties,exit_vs_liquidation,status,hash\n");
        foreach (Run r in runs)
        {
            sb.Append(string.Join(',', r.Symbol, r.Risk, r.Account, r.Variant, r.Bars, r.Trades, F(r.FinalEquity), F(r.MaxDrawdown), r.Detected, r.Affecting, r.RiskTies, r.ExitVersusLiquidation, r.Status, r.Hash)).Append('\n');
        }

        WriteTextAtomically(path, sb.ToString());
    }

    private static void WritePairsCsv(string path, List<Pair> pairs)
    {
        var sb = new StringBuilder("symbol,risk,account,variant,volatility_group,liquidity_group,delta_return_of_capital,relative_final_equity_change,delta_trades,delta_max_drawdown\n");
        foreach (Pair p in pairs)
        {
            sb.Append(string.Join(',', p.Base.Symbol, p.Base.Risk, p.Base.Account, p.Alt.Variant, p.VolatilityGroup, p.LiquidityGroup, F(p.DeltaReturn), F(p.RelativeEquityChange), p.DeltaTrades, F(p.DeltaMaxDrawdown))).Append('\n');
        }

        WriteTextAtomically(path, sb.ToString());
    }

    private static void WriteRegimesCsv(string path, List<RegimeRow> rows)
    {
        var sb = new StringBuilder("symbol,risk,account,variant,year,label,shock,bars,detected,affecting,delta_return_of_capital\n");
        foreach (RegimeRow r in rows)
        {
            sb.Append(string.Join(',', r.Symbol, r.Risk, r.Account, r.Variant, r.Year, r.Label, r.Shock, r.Bars, r.Detected, r.Affecting, F(r.DeltaReturn))).Append('\n');
        }

        WriteTextAtomically(path, sb.ToString());
    }

    private static string Pct(decimal ratio) => (ratio * 100m).ToString("0.000", CultureInfo.InvariantCulture) + "%";

    private static string Pp(double ratio) => (ratio * 100d).ToString("0.000", CultureInfo.InvariantCulture) + " pp";

    private static string Rate(int count, long bars) => bars == 0 ? "n/a" : (count * 10000d / bars).ToString("0.00", CultureInfo.InvariantCulture);

    private static string CountTable(string title, IEnumerable<Run> baseRuns, Func<Run, string> key, string keyName)
    {
        var sb = new StringBuilder($"### {title}\n\n| {keyName} | runs | symbols | bars | detected | affecting | detected /10k bars | affecting /10k bars | AB-1 (SL+TP) | AB-2 (exit vs liquidation) |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (IGrouping<string, Run> g in baseRuns.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            long bars = g.Sum(r => (long)r.Bars);
            sb.Append($"| {g.Key} | {g.Count()} | {g.Select(r => r.Symbol).Distinct().Count()} | {bars} | {g.Sum(r => r.Detected)} | {g.Sum(r => r.Affecting)} | {Rate(g.Sum(r => r.Detected), bars)} | {Rate(g.Sum(r => r.Affecting), bars)} | {g.Sum(r => r.RiskTies)} | {g.Sum(r => r.ExitVersusLiquidation)} |\n");
        }

        return sb.Append('\n').ToString();
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        decimal[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0m : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2m;
    }

    private static string DeltaTable(string title, IEnumerable<Pair> pairs, Func<Pair, string> key, string keyName)
    {
        var sb = new StringBuilder($"### {title}\n\n| {keyName} | runs | runs with a different trade count | variant ended higher | variant ended lower | median abs change of final equity | mean abs change | max abs change | mean delta trades | mean delta max drawdown | max abs delta max drawdown |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (IGrouping<string, Pair> g in pairs.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append($"| {g.Key} | {g.Count()} | {g.Count(p => p.DeltaTrades != 0)} | {g.Count(p => p.RelativeEquityChange > 0m)} | {g.Count(p => p.RelativeEquityChange < 0m)} | {Pct(Median(g.Select(p => Math.Abs(p.RelativeEquityChange))))} | {Pct(g.Average(p => Math.Abs(p.RelativeEquityChange)))} | {Pct(g.Max(p => Math.Abs(p.RelativeEquityChange)))} | {g.Average(p => p.DeltaTrades).ToString("0.000", CultureInfo.InvariantCulture)} | {Pp(g.Average(p => p.DeltaMaxDrawdown))} | {Pp(g.Max(p => Math.Abs(p.DeltaMaxDrawdown)))} |\n");
        }

        return sb.Append('\n').ToString();
    }

    private static string RegimeTable(string title, IEnumerable<RegimeRow> rows, Func<RegimeRow, string> key, string keyName)
    {
        var sb = new StringBuilder($"### {title}\n\n| {keyName} | symbol-years | bars | detected | affecting | detected /10k bars | affecting /10k bars | symbol-years with a different yearly return | median abs change of the yearly return | max abs change |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (IGrouping<string, RegimeRow> g in rows.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            long bars = g.Sum(r => (long)r.Bars);
            sb.Append($"| {g.Key} | {g.Count()} | {bars} | {g.Sum(r => r.Detected)} | {g.Sum(r => r.Affecting)} | {Rate(g.Sum(r => r.Detected), bars)} | {Rate(g.Sum(r => r.Affecting), bars)} | {g.Count(r => r.DeltaReturn != 0m)} | {Pct(Median(g.Select(r => Math.Abs(r.DeltaReturn))))} | {Pct(g.Max(r => Math.Abs(r.DeltaReturn)))} |\n");
        }

        return sb.Append('\n').ToString();
    }

    private static string BuildReport(string dataDirectory, List<Series> series, Dictionary<string, Group> groups, List<Run> runs, List<Pair> pairs, List<RegimeRow> regimes, List<string> skipped)
    {
        var sb = new StringBuilder();
        sb.Append("# Order-dependent bars - measurement report\n\n");
        sb.Append("Purpose: input for the owner's G1 / H2 decision (build `IntrabarPathMode.AdversePath`?). Generated by `BacktestOrderDependentMeasurementTests` (opt-in).\n\n");
        sb.Append("## Reading guide\n\n");
        sb.Append("- **detected**: every bar recorded as order-dependent (AB-1: Stop-Loss and Take-Profit both reached; AB-2: a pending exit order versus a margin liquidation whose winner depends on the vertex order).\n");
        sb.Append("- **affecting**: the subset whose result the configured convention decided (see API_SPECIFICATION \"Order-dependent bars\").\n");
        sb.Append("- **Variants** are compared with the default run (`Base`: DeterministicCandleShape + PricePathConflictMode.Conservative). `ConflictAlt` = PricePathConflictMode.CanonicalOHLC. `PathAlt` = IntrabarPathMode.CanonicalOHLC. AdversePath does not exist yet, so it is NOT measured; the largest of these differences is only an indication of how far a convention can move a result.\n");
        sb.Append("- **change of final equity** = variant final equity / base final equity - 1 (currency-free, so symbols quoted in different currencies are comparable). With compounding, a single different trade can move the final equity by a large factor, so read the median first and the maximum as a warning, not as a typical value. **delta max drawdown** = variant minus base, in percentage points of the application's drawdown ratio. **change of the yearly return** (regime tables) = variant yearly return minus base yearly return.\n");
        sb.Append("- The strategy presets (SMA trend with entry-relative Stop-Loss / Take-Profit) and the two account settings are illustrative, not the owner's strategies. `AppDefault` uses the application defaults (1 unit per trade, so differences are tiny in capital terms); `FullyInvested` uses PercentOfEquity 1 with the application margin ratios; `HighLeverage` is a STRESS setting outside the defaults (PercentOfEquity 1, initial margin ").Append(F(Policy.HighLeverageInitialMargin)).Append(", maintenance margin ").Append(F(Policy.HighLeverageMaintenanceMargin)).Append(") included only to see whether a margin liquidation can race a pending exit order.\n");
        sb.Append("- Regimes are calendar years of one symbol: Up = year return >= ").Append(Pct(Policy.UpYearReturn)).Append(", Down <= ").Append(Pct(Policy.DownYearReturn)).Append(", otherwise Range; Shock = the year has a close-to-close move of at least ").Append(Pct(Policy.ShockDailyMove)).Append(". These labels are a measurement convention, not an application definition.\n\n");

        sb.Append("## Identity (reproduction)\n\n");
        sb.Append($"- Data folder: `{dataDirectory}`; symbols measured: {series.Count}; runs: {runs.Count}; skipped entries: {skipped.Count}\n");
        sb.Append($"- Data fingerprint (SHA-256 of file names, lengths and last write ticks): `{DataFingerprint(dataDirectory, series)}`\n");
        sb.Append($"- BacktestExecutionSemantics.Version: {BacktestExecutionSemantics.Version}\n");
        sb.Append($"- Policy: SMA period {Policy.SmaPeriod}; risk presets {string.Join(", ", Policy.RiskPresets.Select(p => $"{p.Name} (SL {F(p.StopLoss)}, TP {F(p.TakeProfit)})"))}; accounts {string.Join(", ", Policy.Accounts)}; minimum bars {Policy.MinimumBars}; groups by terciles within each market group (JP = tickers ending \"{Policy.JapaneseSuffix}\", {Policy.NoVolumeGroup} = median traded value 0)\n");
        sb.Append($"- Reproducibility check: the first symbol was re-run once; digest, warnings and counts were identical (asserted).\n\n");

        IEnumerable<Run> baseRuns = runs.Where(r => r.Variant == Policy.VariantBase);
        sb.Append("## 1. How often (default conventions)\n\n");
        sb.Append(CountTable("By account and risk preset", baseRuns, r => $"{r.Account} / {r.Risk}", "account / risk"));
        sb.Append(CountTable("By volatility group", baseRuns, r => groups[r.Symbol].VolatilityGroup, "volatility group"));
        sb.Append(CountTable("By liquidity group", baseRuns, r => groups[r.Symbol].LiquidityGroup, "liquidity group"));

        sb.Append("## 2. How much the result moves when the convention changes\n\n");
        foreach (string variant in Policy.Variants.Skip(1))
        {
            IEnumerable<Pair> v = pairs.Where(p => p.Alt.Variant == variant);
            sb.Append(DeltaTable($"{variant}: by account and risk preset", v, p => $"{p.Base.Account} / {p.Base.Risk}", "account / risk"));
            sb.Append(DeltaTable($"{variant}: by volatility group", v, p => p.VolatilityGroup, "volatility group"));
            sb.Append(DeltaTable($"{variant}: by liquidity group", v, p => p.LiquidityGroup, "liquidity group"));
        }

        sb.Append("## 3. By market regime (calendar years)\n\n");
        foreach (string account in Policy.Accounts.Skip(1))
        {
            foreach (string variant in Policy.Variants.Skip(1))
            {
                sb.Append(RegimeTable($"{account}, {variant}: by regime", regimes.Where(r => r.Account == account && r.Variant == variant), r => r.Label + (r.Shock ? " + Shock" : string.Empty), "regime"));
            }
        }

        sb.Append("## 4. Skipped\n\n");
        if (skipped.Count == 0) sb.Append("None.\n");
        foreach (string line in skipped.Take(200)) sb.Append("- ").Append(line).Append('\n');
        if (skipped.Count > 200) sb.Append($"- ... {skipped.Count - 200} more\n");
        return sb.ToString();
    }

    private static string DataFingerprint(string directory, List<Series> series)
    {
        var sb = new StringBuilder();
        foreach (Series s in series.OrderBy(s => s.Symbol, StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(directory, s.Symbol + ".parquet"));
            sb.Append(s.Symbol).Append('|').Append(info.Exists ? info.Length : -1L).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : -1L).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
