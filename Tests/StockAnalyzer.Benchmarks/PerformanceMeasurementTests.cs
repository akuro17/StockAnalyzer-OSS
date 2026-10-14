using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Benchmarks
{
    /// <summary>
    /// Measurement-only records (time and allocated bytes) used to decide whether a performance change is justified.
    /// The assertions only guard against a pathological regression; the numbers go to the test output.
    /// </summary>
    public class PerformanceMeasurementTests
    {
        private const int MeasuredRuns = 7;
        private readonly ITestOutputHelper _output;

        public PerformanceMeasurementTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static List<CandleData> CreateDailyCandles(int count)
        {
            var list = new List<CandleData>(count);
            var date = new DateTime(2000, 1, 3);
            var rng = new Random(42);
            decimal price = 1000m;
            for (int i = 0; i < count; i++)
            {
                price += (decimal)(rng.NextDouble() - 0.5) * 10;
                list.Add(new CandleData(date.AddDays(i), price, price + 5, price - 5, price, 1000 + i));
            }
            return list;
        }

        private (double MedianMs, long MedianBytes) Measure(Action action)
        {
            action(); // JIT warm-up
            var times = new List<double>();
            var bytes = new List<long>();
            for (int run = 0; run < MeasuredRuns; run++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                action();
                sw.Stop();
                bytes.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            bytes.Sort();
            return (times[MeasuredRuns / 2], bytes[MeasuredRuns / 2]);
        }

        [Theory]
        [InlineData(5_000)]
        [InlineData(20_000)]
        public void Measure_AggregateDailyToWeeklyAndMonthly(int dailyCount)
        {
            var candles = CreateDailyCandles(dailyCount);

            var weekly = Measure(() => TimeFrameAggregator.AggregateDailyToWeekly(candles));
            var monthly = Measure(() => TimeFrameAggregator.AggregateDailyToMonthly(candles));

            _output.WriteLine($"[8a] daily={dailyCount} weekly:  median {weekly.MedianMs:F3} ms, {weekly.MedianBytes:N0} bytes");
            _output.WriteLine($"[8a] daily={dailyCount} monthly: median {monthly.MedianMs:F3} ms, {monthly.MedianBytes:N0} bytes");

            Assert.True(weekly.MedianMs < 5_000 && monthly.MedianMs < 5_000);
        }

        [Theory]
        [InlineData(1_000)]
        [InlineData(5_000)]
        public async Task Measure_SavePortfolio_TransactionCount(int transactionCount)
        {
            string root = Path.Combine(Path.GetTempPath(), "sa_perf_portfolio_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var history = new List<Transaction>(transactionCount);
                var start = DateTimeOffset.UtcNow.AddDays(-transactionCount);
                for (int i = 0; i < transactionCount; i++)
                {
                    history.Add(new Transaction(start.AddDays(i), TransactionType.Deposit, null, 0m, 0m, 100m));
                }
                var portfolio = new Portfolio(1000m, new Dictionary<string, Position>(), history);

                using var repository = new UserPortfolioRepository(Path.Combine(root, "portfolio.json"));
                await repository.SavePortfolioAsync(portfolio); // warm-up (creates the database and directory)

                var times = new List<double>();
                var bytes = new List<long>();
                for (int run = 0; run < 5; run++)
                {
                    GC.Collect();
                    long before = GC.GetTotalAllocatedBytes(precise: true);
                    var sw = Stopwatch.StartNew();
                    await repository.SavePortfolioAsync(portfolio);
                    sw.Stop();
                    bytes.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                bytes.Sort();

                _output.WriteLine($"[8b] transactions={transactionCount} save: median {times[2]:F1} ms, {bytes[2]:N0} bytes (process-wide)");
                Assert.True(times[2] < 60_000);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
