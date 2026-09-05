using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Xunit;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Indicators.Standard;
using StockAnalyzer.Core.Models.Indicators.Statistics;


namespace StockAnalyzer.Tests
{
    public class Batch17Tests
    {
        private List<CandleData> CreateTestData(int count, decimal price = 100m)
        {
            var list = new List<CandleData>();
            var date = new DateTime(2023, 1, 1);
            for (int i = 0; i < count; i++)
            {
                list.Add(new CandleData 
                { 
                    Timestamp = date.AddDays(i),
                    Open = price, High = price, Low = price, Close = price, 
                    Volume = 1000
                });
            }
            return list;
        }

        // --- Psychological Line Tests ---
        [Fact]
        public void PsychologicalLine_Calculate_Correctness()
        {
            var candles = new List<CandleData>
            {
                new() { Close = 100m }, new() { Close = 90m }, new() { Close = 80m },
                new() { Close = 90m }, new() { Close = 100m }, new() { Close = 110m }
            };

            var indicator = new PsychologicalLineIndicator(3, Colors.Blue);
            indicator.Calculate(candles);

            Assert.Equal(6, indicator.Values.Count);
            Assert.Null(indicator.Values[2]); 
            
            // i=3: 90>80 (Up), 80<90 (Down), 90<100 (Down prev) -> Window: 90(vs80)=Up. 
            // Wait, logic:
            // i=0: 100 (NA)
            // i=1: 90 < 100 (Down) -> isUp=0
            // i=2: 80 < 90 (Down) -> isUp=0
            // i=3: 90 > 80 (Up) -> isUp=1. Window [1, 2, 3] -> [0, 0, 1] -> 1/3
            decimal expectedVal3 = 100m / 3m; // 33.33..
            Assert.Equal(Math.Round(expectedVal3, 4), Math.Round(indicator.Values[3]!.Value, 4));

            // i=5: 110 > 100 (Up). Window [3,4,5] -> [1,1,1] -> 100%
            Assert.Equal(100m, indicator.Values[5]!.Value);
        }

        // --- MA Deviation Rate Tests ---
        [Fact]
        public void MaDeviation_Calculate_SMA()
        {
            var candles = new List<CandleData>
            {
                new() { Close = 100 }, new() { Close = 101 }, new() { Close = 102 },
                new() { Close = 103 }, new() { Close = 104 }
            };

            var ind = new MaDeviationRateIndicator(3, MovingAverageType.SMA, Colors.Red);
            ind.Calculate(candles);

            // i=2: SMA=(100+101+102)/3=101. Close=102. Dev=(102-101)/101 * 100 = 0.99...
            decimal expected = (1m / 101m) * 100m;
            Assert.Equal(expected, ind.Values[2]!.Value, 4);
        }

        // --- Envelope Tests ---
        [Fact]
        public void Envelope_Calculate_Bands()
        {
            var candles = CreateTestData(10, 100m);
            var ind = new EnvelopeIndicator(5, 0.1m, MovingAverageType.SMA, Colors.Green);
            ind.Calculate(candles);

            // SMA=100. Upper=100*1.1=110. Lower=100*0.9=90.
            Assert.Equal(100m, ind.MiddleBand[5]);
            Assert.Equal(110m, ind.UpperBand[5]);
            Assert.Equal(90m, ind.LowerBand[5]);
        }

        // --- HighLowBand Tests ---
        [Fact]
        public void HighLowBand_Calculate_SMA()
        {
            var candles = CreateTestData(10, 100m);
            // Make variation using with expression
            candles[5] = candles[5] with { High = 110m, Low = 90m };
            
            var ind = new HighLowBandIndicator(3, Colors.Orange); // Period 3
            ind.Calculate(candles);

            // i=5: Window [3,4,5]. All 100 except candle 5. 
            // HighLowBand returns Highest High and Lowest Low (not average)
            // Highs: 100, 100, 110 -> Highest = 110
            // Lows: 100, 100, 90 -> Lowest = 90
            Assert.NotNull(ind.UpperBandValues[5]);
            Assert.Equal(110m, ind.UpperBandValues[5]!.Value);
            Assert.Equal(90m, ind.LowerBandValues[5]!.Value);
        }

        // --- Volume MA Tests ---
        [Fact]
        public void VolumeMA_Calculate_SMA()
        {
            var candles = CreateTestData(5, 100m);
            candles[0] = candles[0] with { Volume = 100 };
            candles[1] = candles[1] with { Volume = 200 };
            candles[2] = candles[2] with { Volume = 300 }; // SMA(3) = 200

            var ind = new VolumeMAIndicator(3, Colors.Blue);
            ind.Calculate(candles);

            Assert.Equal(200m, ind.Values[2]!.Value);
        }

        // --- Volume Profile Tests ---
        [Fact]
        public void VolumeProfile_Calculate_Basic()
        {
            var candles = new List<CandleData>
            {
                new CandleData { Low = 100m, High = 100m, Open=100m, Close=100m, Volume = 1000, Timestamp = DateTime.Now },
                new CandleData { Low = 101m, High = 101m, Open=101m, Close=101m, Volume = 2000, Timestamp = DateTime.Now }, // POC
                new CandleData { Low = 102m, High = 102m, Open=102m, Close=102m, Volume = 500, Timestamp = DateTime.Now }
            };

            var ind = new VolumeProfileIndicator(3, 0, 0.7m, VolumeDistributionMode.Proportional, DisplaySide.Right, Colors.Gray);
            ind.Calculate(candles);

            Assert.Equal(101m, ind.POC); // Max volume at 101
            Assert.Equal(2000m, ind.PriceLevels.Max(p => p.TotalVolume));
        }

        // --- Classic Pivot Tests ---
        [Fact]
        public void ClassicPivot_Calculate_Standard()
        {
            // Day 1: H=200, L=100, C=150.
            // Day 2: Should use Day 1 values. Pivot = (200+100+150)/3 = 150.
            var d1 = new DateTime(2023,1,1);
            var d2 = new DateTime(2023,1,2);
            var candles = new List<CandleData>
            {
                new() { Timestamp=d1, High=200, Low=100, Close=150, Open=100 },
                new() { Timestamp=d2, High=210, Low=110, Close=160, Open=150 }
            };

            var ind = new ClassicPivotPointsIndicator(PivotType.Standard, SourceTimeframe.Daily, Colors.Black);
            ind.Calculate(candles);

            // Index 1 (Day 2) should have Pivot based on Day 1
            Assert.Equal(150m, ind.PivotLine[1]);
            Assert.Null(ind.PivotLine[0]); // Day 1 has no previous data
        }

        /*
        // --- Span Model Tests ---
        [Fact]
        public void SpanModel_Calculate_Alignment()
        {
             var candles = CreateTestData(100);
            
            // 1. Tenkan (9): Range [52..60] -> Max=150, Min=140 -> Mid=145
            candles[60] = candles[60] with { High = 150m, Low = 140m }; 

            // 2. Kijun (26): Range [35..60] -> Max=200, Min=50 -> Mid=125
            candles[40] = candles[40] with { High = 200m, Low = 50m };

            var indicator = new SpanModelIndicator(9, 26, 52, 26, Colors.Red);
            indicator.Calculate(candles);

            // Plotted Index = Calculation Index (60) + Displacement (26) = 86
            int plotIndex = 86;
            
            // Verify SenkouSpanA = (125 + 125) / 2 = 125 (Tenkan min=100 not 140 because base price is 100)
            Assert.NotNull(indicator.SenkouSpanA[plotIndex]);
            Assert.Equal(125m, indicator.SenkouSpanA[plotIndex].Value);
        }
        */
    }
}
