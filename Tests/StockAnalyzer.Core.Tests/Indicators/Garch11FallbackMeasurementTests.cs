#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// Opt-in measurement (WebAiExpansion plan 04, F-4): how often the GARCH(1,1) optimizer result is replaced by the initial point (alpha 0.10, beta 0.85) on the STORED data of each
/// time frame. It reads the Parquet files under the data root named by the environment variable (a folder that holds Daily, Weekly and Monthly), never writes, and asserts only that the
/// data was found. The windows are built exactly as <c>CoreGarch11Indicator</c> does: percent log returns of the close, first fit at bar Period, then every RefitInterval bars.
/// A fit "falls back" when its Alpha and Beta are exactly <see cref="Garch11Math.InitialAlpha"/> and <see cref="Garch11Math.InitialBeta"/>.
/// Runs only when <see cref="DataRootVariable"/> is set; run it alone (<c>--filter</c>).
/// </summary>
public sealed class Garch11FallbackMeasurementTests : IDisposable
{
    private const string DataRootVariable = "SA_GARCH_FALLBACK_DATA_ROOT";
    private const int MaxBars = 1_000_000;
    private const string ParquetPattern = "*.parquet";

    private readonly ITestOutputHelper _output;
    private readonly DuckDBConnectionManager _db = new();

    public Garch11FallbackMeasurementTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _db.Dispose();

    [EnvironmentFact(DataRootVariable)]
    public async Task Measure_FallbackRateOnTheStoredTimeFrames()
    {
        string root = Environment.GetEnvironmentVariable(DataRootVariable)!;
        var settings = new MarketDataSettings
        {
            DailyDataPath = Path.Combine(root, "Daily"),
            WeeklyDataPath = Path.Combine(root, "Weekly"),
            MonthlyDataPath = Path.Combine(root, "Monthly"),
            MetadataPath = Path.Combine(root, "Metadata"),
            TickerListPath = Path.Combine(root, "tickers.json"),
        };
        var service = new ParquetDataService(_db, Options.Create(settings));

        var frames = new (TimeFrame Frame, string Directory)[]
        {
            (TimeFrame.D1, settings.DailyDataPath),
            (TimeFrame.W1, settings.WeeklyDataPath),
            (TimeFrame.MN1, settings.MonthlyDataPath),
        };
        var periods = new[] { IndicatorDefaultConstants.Garch11DefaultPeriod, IndicatorDefaultConstants.Garch11MinPeriod };

        foreach ((TimeFrame frame, string directory) in frames)
        {
            string[] symbols = Directory.GetFiles(directory, ParquetPattern).Select(Path.GetFileNameWithoutExtension).Where(s => s is not null).Select(s => s!).ToArray();
            Assert.NotEmpty(symbols);

            var closes = new List<double[]>();
            foreach (string symbol in symbols)
            {
                IReadOnlyList<CandleData> candles = await service.LoadCandlesAsync(symbol, frame, MaxBars);
                closes.Add(candles.Select(c => (double)c.Close).ToArray());
            }

            long bars = closes.Sum(c => (long)c.Length);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{frame}: {symbols.Length} files, {bars} bars in total, longest {closes.Max(c => c.Length)} bars"));

            foreach (int period in periods)
            {
                Tally tally = Measure(closes, period);
                _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  Period {period}, refit {IndicatorDefaultConstants.Garch11DefaultRefitInterval}, iterations {IndicatorDefaultConstants.Garch11DefaultMaxIterations}: " +
                    $"symbols too short {tally.TooShort}, fits {tally.Fits}, accepted {tally.Accepted}, initial-point fallback {tally.Fallback}, no estimate {tally.NoEstimate}"));
            }
        }
    }

    private static Tally Measure(IReadOnlyList<double[]> closesPerSymbol, int period)
    {
        var tally = new Tally();
        foreach (double[] prices in closesPerSymbol)
        {
            int count = prices.Length;
            if (count <= period)
            {
                tally.TooShort++;
                continue;
            }

            var returns = new double[count];
            returns[0] = double.NaN;
            for (int k = 1; k < count; k++)
            {
                returns[k] = EgarchMath.PercentLogReturn(prices[k - 1], prices[k]);
            }

            for (int j = period; j < count; j += IndicatorDefaultConstants.Garch11DefaultRefitInterval)
            {
                tally.Fits++;
                if (!Garch11Math.TryFit(returns, j - period + 1, period, IndicatorDefaultConstants.Garch11DefaultMaxIterations, out Garch11Fit fit))
                {
                    tally.NoEstimate++;
                }
                else if (fit.Alpha == Garch11Math.InitialAlpha && fit.Beta == Garch11Math.InitialBeta)
                {
                    tally.Fallback++;
                }
                else
                {
                    tally.Accepted++;
                }
            }
        }

        return tally;
    }

    private sealed class Tally
    {
        public int TooShort;
        public long Fits;
        public long Accepted;
        public long Fallback;
        public long NoEstimate;
    }
}
