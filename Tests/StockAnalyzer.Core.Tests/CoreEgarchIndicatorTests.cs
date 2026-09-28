using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreEgarchIndicatorTests
    {
        /// <summary>Prices from a deterministic pseudo-random walk with volatility clustering.</summary>
        private static List<CoreCandleData> CreateCandles(int count, ulong seed = 12345)
        {
            ulong state = seed;
            double Uniform()
            {
                state = unchecked(6364136223846793005UL * state + 1442695040888963407UL);
                return ((state >> 11) + 0.5) / (double)(1UL << 53);
            }

            var candles = new List<CoreCandleData>(count);
            double price = 100.0;
            double lnSigma2 = 0.0;
            double zPrev = 0.0;
            var start = new DateTime(2020, 1, 1);
            for (int i = 0; i < count; i++)
            {
                double z = Math.Sqrt(-2.0 * Math.Log(Uniform())) * Math.Cos(2.0 * Math.PI * Uniform());
                lnSigma2 = 0.02 + 0.15 * (Math.Abs(zPrev) - Math.Sqrt(2.0 / Math.PI)) - 0.08 * zPrev + 0.95 * lnSigma2;
                price *= Math.Exp(0.01 * Math.Exp(0.5 * lnSigma2) * z);
                decimal close = (decimal)price;
                candles.Add(new CoreCandleData(start.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000));
                zPrev = z;
            }
            return candles;
        }

        /// <summary>
        /// Values follow the refit blocks: within a block the valued bars form a prefix (a failed refit leaves none, an unstable one leaves the
        /// bars before its first guarded bar), and the blocks with an empty tail are exactly the failed plus the unstable re-estimations (a fit can
        /// be poor on short windows, so a test must not assume every block is valued). Returns the number of fully valued blocks.
        /// </summary>
        private static int AssertBlockLayout(CoreEgarchIndicator indicator, int period, int refitInterval, int barCount)
        {
            Assert.Equal(barCount, indicator.Values.Count);
            // The first estimate needs Period returns, and no bar uses its own return: the first output is at bar Period + 1.
            Assert.True(indicator.Values.Take(period + 1).All(v => v == null));

            int valued = 0;
            int empty = 0;
            for (int start = period + 1; start < barCount; start += refitInterval)
            {
                var block = indicator.Values.Skip(start).Take(Math.Min(refitInterval, barCount - start)).ToList();
                int valuedPrefix = block.TakeWhile(v => v.HasValue).Count();
                Assert.All(block.Take(valuedPrefix), v => Assert.True(v!.Value > 0m));
                Assert.All(block.Skip(valuedPrefix), v => Assert.Null(v));
                if (valuedPrefix == block.Count)
                {
                    valued++;
                }
                else
                {
                    empty++;
                }
            }

            Assert.Equal(empty, indicator.LastFailedRefitCount + indicator.LastUnstableRefitCount);
            Assert.Equal(valued + empty, indicator.LastRefitCount);
            return valued;
        }

        [Fact]
        public void IsRegisteredInTheFactory()
        {
            var factory = new IndicatorFactory();

            Assert.True(factory.IsRegistered(IndicatorType.Egarch));
            Assert.IsType<CoreEgarchIndicator>(factory.Create(IndicatorType.Egarch));
        }

        [Fact]
        public void Calculate_ReturnsOneValuePerBar_WithLeadingWarmupEmpty()
        {
            var candles = CreateCandles(400);
            var indicator = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.True(AssertBlockLayout(indicator, 100, 20, candles.Count) > 0);
        }

        [Fact]
        public void Calculate_OutputIsPercentPerBar()
        {
            var candles = CreateCandles(400);
            var indicator = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            indicator.Calculate(candles);

            // The generator's daily volatility is about 1 % (0.01 · exp(ln σ² / 2)); the estimate must be of that order, not a fraction (0.01) or annualized (~16).
            decimal mean = indicator.Values.Skip(101).Where(v => v.HasValue).Average(v => v!.Value);
            Assert.InRange(mean, 0.3m, 3m);
        }

        [Fact]
        public void Calculate_IsCausal_ValuesDoNotChangeWhenLaterBarsAreRemoved()
        {
            var candles = CreateCandles(400);
            var full = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            full.Calculate(candles);
            var truncated = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            truncated.Calculate(candles.Take(300).ToList());

            Assert.Equal(full.Values.Take(300), truncated.Values);
        }

        [Fact]
        public void Calculate_ValueAtABarDoesNotDependOnThatBarsOwnPrice()
        {
            var candles = CreateCandles(300);
            var changed = candles.ToList();
            var last = changed[^1];
            changed[^1] = last with { Close = last.Close * 1.5m, High = last.High * 1.5m };

            var a = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            a.Calculate(candles);
            var b = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            b.Calculate(changed);

            Assert.Equal(a.Values, b.Values);
        }

        [Fact]
        public void Calculate_SeriesShorterThanWindow_IsAllEmpty()
        {
            var indicator = new CoreEgarchIndicator { Period = 100 };
            var result = indicator.Calculate(CreateCandles(80));

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_FlatPrices_GivesEmptyValuesNotErrors()
        {
            var candles = Enumerable.Range(0, 200)
                .Select(i => new CoreCandleData(new DateTime(2020, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 1000))
                .ToList();
            var indicator = new CoreEgarchIndicator { Period = 100 };
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Theory]
        [InlineData(50, 1)]
        [InlineData(50, 20)]
        [InlineData(50, 250)]
        public void Calculate_FirstValueIsAtBarPeriodPlusOne_AndValuesFollowTheRefitBlocks(int period, int refitInterval)
        {
            var candles = CreateCandles(400);
            var indicator = new CoreEgarchIndicator { Period = period, RefitInterval = refitInterval };
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            AssertBlockLayout(indicator, period, refitInterval, candles.Count);
        }

        [Theory]
        [InlineData(50, 0)]
        [InlineData(51, 0)]
        [InlineData(52, 1)]
        [InlineData(53, 2)]
        public void Calculate_SeriesLengthAroundPeriod_HasExpectedNumberOfValues(int length, int expectedValues)
        {
            var indicator = new CoreEgarchIndicator { Period = 50, RefitInterval = 20 };
            var result = indicator.Calculate(CreateCandles(length));

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(length, indicator.Values.Count);
            Assert.Equal(expectedValues, indicator.Values.Count(v => v.HasValue));
        }

        [Fact]
        public void Calculate_LastBarOnRefitBoundary_IsItsOwnBlockAndCountsAllRefits()
        {
            // Refit bars are 51, 71, 91 for Period 50 / interval 20; a 92-bar series ends exactly on the third refit bar,
            // which forms a block of one bar: it is valued exactly when that refit was usable.
            var indicator = new CoreEgarchIndicator { Period = 50, RefitInterval = 20 };
            indicator.Calculate(CreateCandles(92));

            Assert.Equal(3, indicator.LastRefitCount);
            AssertBlockLayout(indicator, 50, 20, 92);
            Assert.Equal(indicator.Values.Count(v => v.HasValue), indicator.Values.Skip(51).Count(v => v.HasValue));
        }

        [Fact]
        public void Calculate_RefitIntervalOne_IsCausalWhenLaterBarsAreRemoved()
        {
            var candles = CreateCandles(200);
            var full = new CoreEgarchIndicator { Period = 50, RefitInterval = 1 };
            full.Calculate(candles);
            var truncated = new CoreEgarchIndicator { Period = 50, RefitInterval = 1 };
            truncated.Calculate(candles.Take(150).ToList());

            Assert.Equal(full.Values.Take(150), truncated.Values);
        }

        [Fact]
        public void Calculate_ChangingAPriceChangesOnlyLaterBars()
        {
            // Poison bar 300 of 400: bars up to and including 300 must be identical (a bar never sees its own or later prices),
            // and the estimate must react afterwards, so the test cannot pass vacuously.
            var candles = CreateCandles(400);
            var poisoned = candles.ToList();
            poisoned[300] = poisoned[300] with { Close = poisoned[300].Close * 1.4m, High = poisoned[300].High * 1.4m };

            var a = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            a.Calculate(candles);
            var b = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            b.Calculate(poisoned);

            Assert.Equal(a.Values.Take(301), b.Values.Take(301));
            Assert.NotEqual(a.Values.Skip(301), b.Values.Skip(301));
        }

        [Fact]
        public void Diagnostics_GoodData_CountsRefitsWithoutFailures()
        {
            var indicator = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            indicator.Calculate(CreateCandles(400));

            // Refit bars 101, 121, ..., 381 -> 15 refits.
            Assert.Equal(15, indicator.LastRefitCount);
            Assert.Equal(0, indicator.LastFailedRefitCount);
        }

        [Fact]
        public void Diagnostics_FlatPrices_ReportEveryRefitAsFailed()
        {
            var candles = Enumerable.Range(0, 200)
                .Select(i => new CoreCandleData(new DateTime(2020, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 1000))
                .ToList();
            var indicator = new CoreEgarchIndicator { Period = 100, RefitInterval = 20 };
            indicator.Calculate(candles);

            Assert.True(indicator.LastRefitCount > 0);
            Assert.Equal(indicator.LastRefitCount, indicator.LastFailedRefitCount);
            Assert.All(indicator.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_InvalidParameters_ReturnsFailure()
        {
            var indicator = new CoreEgarchIndicator { P = 0 };
            var result = indicator.Calculate(CreateCandles(300));

            Assert.False(result.IsSuccessful);
        }

        [Fact]
        public void Calculate_HigherOrders_Works()
        {
            var indicator = new CoreEgarchIndicator { P = 2, Q = 2, Period = 150, RefitInterval = 50 };
            var result = indicator.Calculate(CreateCandles(400));

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Contains(indicator.Values, v => v.HasValue);
        }

        [Fact]
        public void Configure_AppliesParameters()
        {
            var indicator = new CoreEgarchIndicator();
            indicator.Configure(new CoreEgarchParameter { P = 2, Q = 3, Period = 300, RefitInterval = 10, MaxIterations = 500, MaxSigmaRatio = 20m, MinSigmaRatio = 0.05m });

            Assert.Equal(2, indicator.P);
            Assert.Equal(3, indicator.Q);
            Assert.Equal(300, indicator.Period);
            Assert.Equal(10, indicator.RefitInterval);
            Assert.Equal(500, indicator.MaxIterations);
            Assert.Equal(20m, indicator.MaxSigmaRatio);
            Assert.Equal(0.05m, indicator.MinSigmaRatio);
            Assert.Equal("EGARCH(2,3)", indicator.ShortName);
        }

        [Theory]
        [InlineData(nameof(CoreEgarchParameter.MaxSigmaRatio), 1.9, true)]
        [InlineData(nameof(CoreEgarchParameter.MaxSigmaRatio), 100001.0, true)]
        [InlineData(nameof(CoreEgarchParameter.MaxSigmaRatio), 2.0, false)]
        [InlineData(nameof(CoreEgarchParameter.MaxSigmaRatio), 100000.0, false)]
        [InlineData(nameof(CoreEgarchParameter.MinSigmaRatio), -0.01, true)]
        [InlineData(nameof(CoreEgarchParameter.MinSigmaRatio), 0.51, true)]
        [InlineData(nameof(CoreEgarchParameter.MinSigmaRatio), 0.0, false)]
        [InlineData(nameof(CoreEgarchParameter.MinSigmaRatio), 0.5, false)]
        public void SigmaRatioParameters_ReportOutOfRangeValuesAsDataErrors(string property, double value, bool expectError)
        {
            var parameter = new CoreEgarchParameter();
            typeof(CoreEgarchParameter).GetProperty(property)!.SetValue(parameter, (decimal)value);

            var errors = ((System.ComponentModel.INotifyDataErrorInfo)parameter).GetErrors(property).Cast<object>().ToList();

            Assert.Equal(expectError, errors.Count > 0);
            Assert.Equal(expectError, parameter.HasErrors);
        }

        [Fact]
        public void ParameterValidate_RejectsOutOfRangeValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEgarchParameter { Period = 10 }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEgarchParameter { Q = 4 }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEgarchParameter { MaxSigmaRatio = 1m }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEgarchParameter { MaxSigmaRatio = 100001m }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEgarchParameter { MinSigmaRatio = -0.1m }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEgarchParameter { MinSigmaRatio = 0.6m }.Validate());
            new CoreEgarchParameter().Validate();
            new CoreEgarchParameter { MinSigmaRatio = 0m, MaxSigmaRatio = 100000m }.Validate(); // both limits off
        }

        /// <summary>
        /// 2681-T daily closes (bars 260..530, 7 significant digits as the app receives them). With Period 250 the single refit block
        /// (2023-02-06..2023-03-06) has a fitted filter that collapses sigma to the lower guard (shown as 0.000) and then runs into the upper
        /// guard (shown as 49466.274); the block must be empty from the first guarded bar on instead (earlier bars keep their values).
        /// </summary>
        private static readonly double[] DivergingBlockCloses =
        {
            1073.315, 1023.35, 1048.332, 1047.407, 1055.735, 1068.688, 1054.809, 1064.062, 1070.539, 1068.688,
            1068.688, 1113.101, 1267.622, 1388.832, 1400.861, 1399.01, 1436.946, 1399.01, 1430.469, 1403.636,
            1380.505, 1404.562, 1381.43, 1344.419, 1321.287, 1267.622, 1258.369, 1247.266, 1238.938, 1270.397,
            1268.547, 1269.472, 1309.259, 1306.483, 1300.931, 1256.518, 1220.433, 1225.059, 1221.358, 1184.347,
            1181.571, 1189.899, 1200.173, 1169.351, 1196.437, 1212.314, 1216.05, 1216.984, 1212.314, 1202.04,
            1179.625, 1167.483, 1188.031, 1192.701, 1167.483, 1166.549, 1170.285, 1184.295, 1198.305, 1198.305,
            1172.153, 1174.021, 1151.605, 1171.219, 1171.219, 1175.889, 1182.427, 1198.305, 1202.04, 1160.011,
            1184.295, 1103.038, 1088.094, 1121.718, 1121.718, 1112.378, 1124.52, 1118.916, 1100.236, 1105.84,
            1099.302, 1112.378, 1138.53, 1168.417, 1186.163, 1167.483, 1193.635, 1196.437, 1177.757, 1170.285,
            1163.747, 1141.331, 1126.388, 1119.85, 1120.784, 1117.982, 1121.718, 1122.652, 1127.322, 1144.134,
            1136.662, 1135.728, 1154.407, 1165.615, 1153.473, 1143.199, 1177.757, 1188.965, 1183.361, 1185.229,
            1190.833, 1218.852, 1208.579, 1212.314, 1267.42, 1258.08, 1250.608, 1282.364, 1301.977, 1301.043,
            1315.987, 1303.845, 1284.231, 1318.789, 1311.317, 1334.667, 1294.505, 1269.288, 1320.657, 1327.195,
            1350.545, 1337.469, 1318.789, 1525.2, 1426.197, 1484.105, 1490.643, 1482.236, 1477.567, 1495.312,
            1459.821, 1447.679, 1440.207, 1472.897, 1515.86, 1550.418, 1542.012, 1527.068, 1505.586, 1544.814,
            1540.144, 1567.229, 1578.437, 1606.457, 1620.466, 1595.249, 1558.824, 1570.965, 1581.239, 1595.249,
            1581.239, 1626.071, 1692.384, 1680.242, 1618.599, 1660.921, 1666.564, 1668.445, 1747.447, 1750.269,
            1743.685, 1758.733, 1776.602, 1716.41, 1745.566, 1832.092, 1817.984, 1880.998, 1868.771, 1840.556,
            1845.259, 1795.412, 1824.568, 1829.27, 1843.378, 1831.151, 1830.211, 1827.389, 1817.984, 1812.341,
            1929.904, 1857.485, 1863.128, 1857.485, 1855.604, 1590.384, 1535.835, 1549.002, 1605.432, 1679.731,
            1713.589, 1665.623, 1691.958, 1709.827, 1760.614, 1785.067, 1844.318, 1775.662, 1783.186, 1785.067,
            1733.339, 1744.625, 1769.078, 1845.259, 1884.76, 1916.737, 1835.854, 1865.009, 1865.95, 1865.95,
            1817.044, 1833.973, 1974.107, 1948.714, 2040.882, 2149.98, 2091.669, 2063.455, 2050.287, 1997.62,
            1935.547, 1919.558, 1859.366, 1868.771, 1815.163, 1854.664, 1765.316, 1790.71, 1745.566, 1760.614,
            1759.673, 1737.101, 1726.756, 1734.28, 1758.733, 1756.852, 1771.9, 1782.245, 1757.792, 1735.22,
            1713.589, 1735.22, 1713.589, 1684.433, 1674.088, 1663.742, 1649.635, 1691.958, 1631.765, 1658.1,
            1670.326, 1661.861, 1609.193, 1569.693, 1562.169, 1546.18, 1522.668, 1505.739, 1596.027, 1571.573,
            1635.528
        };

        [Fact]
        public void Calculate_BlockWhoseFilterHitsAVarianceGuard_ShowsNoValuesForThatBlock()
        {
            var start = new DateTime(2023, 1, 1);
            var candles = DivergingBlockCloses
                .Select((c, i) => new CoreCandleData(start.AddDays(i), (decimal)c, (decimal)c, (decimal)c, (decimal)c, 1000))
                .ToList();
            var indicator = new CoreEgarchIndicator { Period = 250, RefitInterval = 20 };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(candles.Count, indicator.Values.Count);
            Assert.Equal(1, indicator.LastRefitCount);
            Assert.Equal(0, indicator.LastFailedRefitCount);
            Assert.Equal(1, indicator.LastUnstableRefitCount);

            // Block bars are 251..270 (2023-02-06..2023-03-06). Bar 263 (2023-02-22) has collapsed to 0.5 % of the window's RMS return, below the
            // default Min Sigma Ratio, so it is empty on its own; sigma first reaches the lower guard on 2023-02-24 (bar 264) and the rest is empty.
            Assert.All(indicator.Values.Take(251), v => Assert.Null(v));
            Assert.All(indicator.Values.Skip(251).Take(12), v => Assert.True(v.HasValue && v.Value > 0m && v.Value < 100m));
            Assert.All(indicator.Values.Skip(263), v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_GuardedBlockKeepsItsEarlyBarsWhateverFollows()
        {
            // Causality: cutting the series at bar 263 (before the first empty bar) must not change the bars that were valued.
            var start = new DateTime(2023, 1, 1);
            CoreCandleData Candle(int i) => new(start.AddDays(i), (decimal)DivergingBlockCloses[i], (decimal)DivergingBlockCloses[i], (decimal)DivergingBlockCloses[i], (decimal)DivergingBlockCloses[i], 1000);
            var full = new CoreEgarchIndicator { Period = 250, RefitInterval = 20 };
            full.Calculate(Enumerable.Range(0, DivergingBlockCloses.Length).Select(Candle).ToList());
            var cut = new CoreEgarchIndicator { Period = 250, RefitInterval = 20 };
            cut.Calculate(Enumerable.Range(0, 263).Select(Candle).ToList());

            Assert.Equal(full.Values.Take(263), cut.Values);
            Assert.Equal(0, cut.LastUnstableRefitCount);
        }

        [Theory]
        [InlineData(50)]
        [InlineData(100)]
        public void Diagnostics_EmptyBlocksAreExactlyTheFailedPlusUnstableRefits(int period)
        {
            var indicator = new CoreEgarchIndicator { Period = period, RefitInterval = 20 };

            indicator.Calculate(CreateCandles(400));

            AssertBlockLayout(indicator, period, 20, 400);
        }

        /// <summary>
        /// 3382-T daily closes (bars 1760..2030, 7 significant digits). The single refit block (2024-08-07..2024-09-04) never touches a variance guard,
        /// yet after the August 2024 crash and rebound sigma jumps to about 23,558 on 2024-08-21 (14,563 x the window's RMS return) for one bar.
        /// </summary>
        private static readonly double[] RunAwayBarCloses =
        {
            1848.978, 1864.98, 1824.819, 1807.249, 1828.584, 1834.546, 1870.941, 1896.356, 1898.239, 1934.321,
            1918.947, 1906.71, 1894.16, 1852.744, 1869.687, 1883.806, 1888.512, 1912.985, 1885.06, 1891.022,
            1891.65, 1877.872, 1894.342, 1898.142, 1888.324, 1898.459, 1897.826, 1904.16, 1899.409, 1891.808,
            1905.744, 1964.972, 1947.868, 1915.562, 1922.847, 1942.484, 1907.644, 1867.737, 1894.342, 1901.627,
            1901.31, 1888.957, 1854.434, 1829.096, 1809.142, 1797.423, 1823.712, 1833.53, 1858.552, 1837.648,
            1823.712, 1741.996, 1687.202, 1678.334, 1691.637, 1697.338, 1655.53, 1656.163, 1671.049, 1709.374,
            1714.758, 1728.694, 1693.22, 1739.779, 1741.996, 1747.698, 1780.003, 1780.003, 1786.021, 1803.441,
            1801.858, 1792.356, 1823.712, 1856.334, 1813.576, 1831.313, 1793.623, 1794.89, 1800.274, 1819.594,
            1835.431, 1811.359, 1741.996, 1740.413, 1832.897, 1827.512, 1827.512, 1862.669, 1818.327, 1814.843,
            1833.847, 1846.199, 1821.178, 1802.491, 1737.879, 1704.306, 1715.391, 1738.829, 1736.929, 1757.833,
            1761, 1758.783, 1774.936, 1772.085, 1772.085, 1787.922, 1787.922, 1816.11, 1809.142, 1855.068,
            1805.975, 1824.978, 1821.178, 1833.214, 1789.505, 1806.925, 1834.164, 1811.043, 1802.808, 1832.263,
            1837.964, 1859.502, 1848.1, 1854.751, 1860.135, 1870.904, 1912.712, 1933.616, 1923.797, 1920.947,
            1955.153, 1973.207, 1989.043, 1958.004, 1955.47, 1983.659, 2021.349, 1996.011, 2009.947, 2033.068,
            2011.214, 2013.291, 2137.923, 2099.096, 2084.715, 2088.071, 2090.467, 2119.228, 2065.541, 2016.647,
            2004.183, 2010.415, 2041.093, 2047.804, 2055.474, 2081.839, 2102.93, 2098.137, 2050.202, 2066.979,
            2088.071, 2109.162, 2123.063, 2054.995, 2041.573, 2043.97, 2041.093, 2051.639, 2069.855, 2089.988,
            2056.433, 1957.686, 1916.941, 1917.42, 1893.452, 1869.964, 1889.138, 1913.106, 1947.14, 1948.099,
            1963.438, 1965.356, 1955.289, 1955.768, 1966.314, 1943.785, 1921.255, 1899.205, 1931.321, 1942.826,
            1935.156, 1940.429, 1927.966, 1939.471, 1940.908, 1952.892, 1955.768, 1946.181, 1946.181, 1941.388,
            1951.454, 1943.785, 1941.388, 1946.661, 1944.264, 1949.058, 1961.041, 1974.463, 1977.34, 1969.19,
            1963.438, 1966.314, 1950.975, 1942.826, 1922.214, 1915.023, 1871.881, 1832.574, 1859.418, 1863.732,
            1859.897, 1883.386, 1900.643, 1879.072, 1879.072, 1879.072, 1892.014, 1894.411, 1912.147, 1911.188,
            1892.014, 1872.84, 1854.625, 1859.897, 1739.1, 1711.777, 1730.472, 1735.265, 1726.637, 1723.761,
            1722.802, 1692.123, 1683.495, 1658.568, 1702.19, 1721.843, 1735.745, 1737.182, 1717.05, 1542.565,
            1633.163, 1636.518, 1612.071, 1603.443, 1651.378, 1672.47, 1684.933, 1688.288, 2071.772, 1853.187,
            1960.083, 1931.801, 1960.083, 1953.851, 2034.383, 2013.291, 2049.588, 2031.198, 2087.338, 2138.154,
            2118.795
        };

        private static List<CoreCandleData> RunAwayBarCandles()
        {
            var start = new DateTime(2024, 1, 1);
            return RunAwayBarCloses
                .Select((c, i) => new CoreCandleData(start.AddDays(i), (decimal)c, (decimal)c, (decimal)c, (decimal)c, 1000))
                .ToList();
        }

        [Fact]
        public void Calculate_RunAwayBarWithoutGuardTouch_IsLeftEmptyByTheDefaultLimits_AndOnlyThatBar()
        {
            var indicator = new CoreEgarchIndicator { Period = 250, RefitInterval = 20 };

            var result = indicator.Calculate(RunAwayBarCandles());

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(1, indicator.LastRefitCount);
            Assert.Equal(1, indicator.LastUnstableRefitCount);
            // Block bars are 251..270; the run-away is bar 260 (2024-08-21). Every other bar of the block keeps its value.
            Assert.Null(indicator.Values[260]);
            for (int bar = 251; bar <= 270; bar++)
            {
                if (bar != 260)
                {
                    Assert.True(indicator.Values[bar].HasValue, $"bar {bar}");
                }
            }
        }

        [Fact]
        public void Calculate_WithBothLimitsOff_ShowsTheRunAwayValue()
        {
            var indicator = new CoreEgarchIndicator { Period = 250, RefitInterval = 20, MaxSigmaRatio = 100000m, MinSigmaRatio = 0m };

            indicator.Calculate(RunAwayBarCandles());

            Assert.Equal(0, indicator.LastUnstableRefitCount);
            Assert.True(indicator.Values[260] > 1000m);
        }

        [Fact]
        public void Calculate_LimitsAreCausal_CuttingBeforeTheRunAwayBarChangesNothing()
        {
            var candles = RunAwayBarCandles();
            var full = new CoreEgarchIndicator { Period = 250, RefitInterval = 20 };
            full.Calculate(candles);
            var cut = new CoreEgarchIndicator { Period = 250, RefitInterval = 20 };
            cut.Calculate(candles.Take(260).ToList());

            Assert.Equal(full.Values.Take(260), cut.Values);
        }
    }
}
