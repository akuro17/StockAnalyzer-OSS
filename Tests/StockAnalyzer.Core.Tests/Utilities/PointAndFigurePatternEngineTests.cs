using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Utilities
{
    public class PointAndFigurePatternEngineTests
    {
        [Fact]
        public void Analyze_DoubleTopBreakout_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            var columns = new List<CoreCandleData>
            {
                MakeUpColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 10m, 15m), // X column: 10 to 15 (High=15)
                MakeDownColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 14m, 12m), // O column: 14 to 12
                MakeUpColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 13m, 16m)  // X column: 13 to 16 (Breaks 15)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            Assert.Single(signals);
            var signal = signals.First();
            Assert.Equal(PnfPatternType.DoubleTopBreakout, signal.PatternType);
            Assert.True(signal.IsBullish);
            Assert.Equal(15m, signal.SignalLevel);
            Assert.Equal(16m, signal.TriggerPrice); // 1 box above prev high
            Assert.Equal(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), signal.TriggerTimestamp);
        }

        [Fact]
        public void Analyze_DoubleBottomBreakout_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            var columns = new List<CoreCandleData>
            {
                MakeDownColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 20m, 15m), // O column: 20 to 15 (Low=15)
                MakeUpColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 16m, 18m),   // X column: 16 to 18
                MakeDownColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 17m, 14m)  // O column: 17 to 14 (Breaks 15)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            Assert.Single(signals);
            var signal = signals.First();
            Assert.Equal(PnfPatternType.DoubleBottomBreakout, signal.PatternType);
            Assert.False(signal.IsBullish);
            Assert.Equal(15m, signal.SignalLevel);
            Assert.Equal(14m, signal.TriggerPrice); // 1 box below prev low
            Assert.Equal(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), signal.TriggerTimestamp);
        }

        [Fact]
        public void Analyze_NoBreakout_ReturnsEmpty()
        {
            // Arrange
            decimal boxSize = 1m;
            var columns = new List<CoreCandleData>
            {
                MakeUpColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 10m, 15m), // X column: 10 to 15 
                MakeDownColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 14m, 12m), // O column: 14 to 12
                MakeUpColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 13m, 14m)  // X column: 13 to 14 (No breakout)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            Assert.Empty(signals);
        }

        [Fact]
        public void Analyze_BullishSupportLineBreakout_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            var columns = new List<CoreCandleData>
            {
                MakeDownColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 20m, 15m), // i=0, anchor at 15
                MakeUpColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 16m, 19m),   // i=1
                MakeDownColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 18m, 17m), // i=2, line0=17, passes
                MakeUpColumn(DateTime.Parse("2023-01-04", System.Globalization.CultureInfo.InvariantCulture), 18m, 21m),   // i=3
                MakeDownColumn(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), 20m, 18m)  // i=4, line0=19, breaks (18 < 19)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            var supportBreaks = signals.Where(s => s.PatternType == PnfPatternType.BullishSupportLineBreakout).ToList();
            Assert.Single(supportBreaks);
            var signal = supportBreaks.First();
            Assert.False(signal.IsBullish);
            Assert.Equal(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), signal.TriggerTimestamp);
            Assert.Equal(19m, signal.SignalLevel);
            Assert.Equal(18m, signal.TriggerPrice);
        }

        [Fact]
        public void Analyze_BearishResistanceLineBreakout_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            var columns = new List<CoreCandleData>
            {
                MakeUpColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 15m, 20m),   // i=0, anchor at 20
                MakeDownColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 19m, 16m), // i=1
                MakeUpColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 17m, 18m),   // i=2, line0=18, passes
                MakeDownColumn(DateTime.Parse("2023-01-04", System.Globalization.CultureInfo.InvariantCulture), 17m, 14m), // i=3
                MakeUpColumn(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), 15m, 18m)    // i=4, line0=16, breaks (18 > 16)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            var resistanceBreaks = signals.Where(s => s.PatternType == PnfPatternType.BearishResistanceLineBreakout).ToList();
            Assert.Single(resistanceBreaks);
            var signal = resistanceBreaks.First();
            Assert.True(signal.IsBullish);
            Assert.Equal(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), signal.TriggerTimestamp);
            Assert.Equal(16m, signal.SignalLevel);
            Assert.Equal(17m, signal.TriggerPrice);
        }

        [Fact]
        public void Analyze_TripleTopBreakout_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            var columns = new List<CoreCandleData>
            {
                MakeUpColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 10m, 15m), // i=0: X col, High 15
                MakeDownColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 14m, 12m), // i=1: O col
                MakeUpColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 13m, 15m), // i=2: X col, High 15 (Equal high)
                MakeDownColumn(DateTime.Parse("2023-01-04", System.Globalization.CultureInfo.InvariantCulture), 14m, 12m), // i=3: O col
                MakeUpColumn(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), 13m, 16m)  // i=4: X col, High 16 (Breaks 15)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            var tripleTops = signals.Where(s => s.PatternType == PnfPatternType.TripleTopBreakout).ToList();
            Assert.Single(tripleTops);
            var signal = tripleTops.First();
            Assert.True(signal.IsBullish);
            Assert.Equal(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), signal.TriggerTimestamp);
            Assert.Equal(15m, signal.SignalLevel);
            Assert.Equal(16m, signal.TriggerPrice);

            // Ensure we didn't also emit a Double Top for this exact same break
            var doubleTops = signals.Where(s => s.PatternType == PnfPatternType.DoubleTopBreakout).ToList();
            Assert.Empty(doubleTops);
        }

        [Fact]
        public void Analyze_BullishTriangleBreakout_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            // Triangle: Lower highs on X, Higher lows on O, then a breakout
            var columns = new List<CoreCandleData>
            {
                MakeUpColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 10m, 20m), // i=0: X col, High 20
                MakeDownColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 19m, 12m), // i=1: O col, Low 12
                MakeUpColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 13m, 18m), // i=2: X col, High 18 (Lower high)
                MakeDownColumn(DateTime.Parse("2023-01-04", System.Globalization.CultureInfo.InvariantCulture), 17m, 14m), // i=3: O col, Low 14 (Higher low)
                MakeUpColumn(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), 15m, 19m)  // i=4: X col, High 19 (Breaks 18)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            var triangles = signals.Where(s => s.PatternType == PnfPatternType.BullishTriangleBreakout).ToList();
            Assert.Single(triangles);
            var signal = triangles.First();
            Assert.True(signal.IsBullish);
            Assert.Equal(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), signal.TriggerTimestamp);
            // It breaks the most recent X column high
            Assert.Equal(18m, signal.SignalLevel); 
            Assert.Equal(19m, signal.TriggerPrice);
        }

        [Fact]
        public void Analyze_BearishTriangleBreakout_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            // Triangle: Lower highs on X, Higher lows on O, then a breakdown
            var columns = new List<CoreCandleData>
            {
                MakeDownColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 20m, 10m), // i=0: O col, Low 10
                MakeUpColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 12m, 19m), // i=1: X col, High 19
                MakeDownColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 18m, 13m), // i=2: O col, Low 13 (Higher low)
                MakeUpColumn(DateTime.Parse("2023-01-04", System.Globalization.CultureInfo.InvariantCulture), 14m, 17m), // i=3: X col, High 17 (Lower high)
                MakeDownColumn(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), 15m, 12m)  // i=4: O col, Low 12 (Breaks 13)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            var triangles = signals.Where(s => s.PatternType == PnfPatternType.BearishTriangleBreakout).ToList();
            Assert.Single(triangles);
            var signal = triangles.First();
            Assert.False(signal.IsBullish);
            Assert.Equal(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), signal.TriggerTimestamp);
            // It breaks the most recent O column low
            Assert.Equal(13m, signal.SignalLevel); 
            Assert.Equal(12m, signal.TriggerPrice);
        }

        [Fact]
        public void Analyze_BullishCatapult_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            // 1. Triple Top Breakout (1st wave)
            // 2. Pullback
            // 3. Double Top Breakout of the prev peak (2nd wave -> Catapult)
            var columns = new List<CoreCandleData>
            {
                MakeUpColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 10m, 15m),   // i=0: X col, High 15
                MakeDownColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 14m, 12m), // i=1: O col
                MakeUpColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 13m, 15m),   // i=2: X col, High 15
                MakeDownColumn(DateTime.Parse("2023-01-04", System.Globalization.CultureInfo.InvariantCulture), 14m, 12m), // i=3: O col
                MakeUpColumn(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), 13m, 17m),   // i=4: X col, High 17 (Triple Top Breakout)
                MakeDownColumn(DateTime.Parse("2023-01-06", System.Globalization.CultureInfo.InvariantCulture), 16m, 14m), // i=5: O col
                MakeUpColumn(DateTime.Parse("2023-01-07", System.Globalization.CultureInfo.InvariantCulture), 15m, 18m)    // i=6: X col, High 18 (Breaks 17 -> Catapult)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            // Should have Triple Top at i=4 and Catapult at i=6
            var tripleTops = signals.Where(s => s.PatternType == PnfPatternType.TripleTopBreakout).ToList();
            var catapults = signals.Where(s => s.PatternType == PnfPatternType.BullishCatapult).ToList();

            Assert.Single(tripleTops);
            Assert.Equal(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), tripleTops[0].TriggerTimestamp);

            Assert.Single(catapults);
            Assert.Equal(DateTime.Parse("2023-01-07", System.Globalization.CultureInfo.InvariantCulture), catapults[0].TriggerTimestamp);
            Assert.Equal(17m, catapults[0].SignalLevel);
            Assert.Equal(18m, catapults[0].TriggerPrice);
        }

        [Fact]
        public void Analyze_BearishCatapult_Detected()
        {
            // Arrange
            decimal boxSize = 1m;
            // 1. Triple Bottom Breakout (1st wave)
            // 2. Pullback
            // 3. Double Bottom Breakout of the prev trough (2nd wave -> Catapult)
            var columns = new List<CoreCandleData>
            {
                MakeDownColumn(DateTime.Parse("2023-01-01", System.Globalization.CultureInfo.InvariantCulture), 20m, 15m), // i=0: O col, Low 15
                MakeUpColumn(DateTime.Parse("2023-01-02", System.Globalization.CultureInfo.InvariantCulture), 16m, 18m),   // i=1: X col
                MakeDownColumn(DateTime.Parse("2023-01-03", System.Globalization.CultureInfo.InvariantCulture), 17m, 15m), // i=2: O col, Low 15
                MakeUpColumn(DateTime.Parse("2023-01-04", System.Globalization.CultureInfo.InvariantCulture), 16m, 18m),   // i=3: X col
                MakeDownColumn(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), 17m, 13m), // i=4: O col, Low 13 (Triple Bottom Breakout)
                MakeUpColumn(DateTime.Parse("2023-01-06", System.Globalization.CultureInfo.InvariantCulture), 14m, 16m),   // i=5: X col
                MakeDownColumn(DateTime.Parse("2023-01-07", System.Globalization.CultureInfo.InvariantCulture), 15m, 12m)  // i=6: O col, Low 12 (Breaks 13 -> Catapult)
            };

            // Act
            var analysis = PointAndFigurePatternEngine.Analyze(columns, boxSize);
            var signals = analysis.Signals;

            // Assert
            var tripleBottoms = signals.Where(s => s.PatternType == PnfPatternType.TripleBottomBreakout).ToList();
            var catapults = signals.Where(s => s.PatternType == PnfPatternType.BearishCatapult).ToList();

            Assert.Single(tripleBottoms);
            Assert.Equal(DateTime.Parse("2023-01-05", System.Globalization.CultureInfo.InvariantCulture), tripleBottoms[0].TriggerTimestamp);

            Assert.Single(catapults);
            Assert.Equal(DateTime.Parse("2023-01-07", System.Globalization.CultureInfo.InvariantCulture), catapults[0].TriggerTimestamp);
            Assert.Equal(13m, catapults[0].SignalLevel);
            Assert.Equal(12m, catapults[0].TriggerPrice);
        }

        // Helper methods matching PointAndFigureConverter logic
        private static CoreCandleData MakeUpColumn(DateTime timestamp, decimal bottom, decimal top)
        {
            return new CoreCandleData(timestamp, bottom, top, bottom, top, 0);
        }

        private static CoreCandleData MakeDownColumn(DateTime timestamp, decimal top, decimal bottom)
        {
            return new CoreCandleData(timestamp, top, top, bottom, bottom, 0);
        }
    }
}
