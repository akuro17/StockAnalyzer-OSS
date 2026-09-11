using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services
{
    public class MagnetSnapTests
    {
        [Fact]
        public void GetMagnetSnap_ShouldSnap_WhenCloseToOHLC_TimeMode()
        {
            // Arrange
            var now = DateTime.Now;
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < 100; i++)
            {
                candles.Add(new CoreCandleData(now.AddMinutes(i), 100, 110, 90, 105, 1000));
            }

            var width = 1000.0;
            var height = 500.0;
            var transform = new GenericCoordinateTransform(ChartAxisMode.Time, width, height);
            
            // Set visible range to middle 50 candles
            var minTime = candles[25].Timestamp;
            var maxTime = candles[75].Timestamp;
            transform.SetTimeRange(minTime, maxTime);
            transform.SetPriceRange(80, 120);

            var service = new MagnetSnapService();

            // Act
            // Target candle index 50
            var targetCandle = candles[50];
            var targetPointChart = new ChartPoint(targetCandle.Timestamp, targetCandle.Close); // Close = 105
            var targetPointScreen = transform.ChartToScreen(targetPointChart);

            // Simulate mouse close to this point (e.g. 2 pixels away)
            var mousePoint = new global::Avalonia.Point(targetPointScreen.X + 2, targetPointScreen.Y + 2);

            var result = service.GetMagnetSnap(mousePoint, candles, transform);

            // Assert
            Assert.True(result.IsSnapped, "Should be snapped");
            Assert.Equal(targetCandle.Close, result.SnappedChartPoint.Price); // Should snap to Close
            Assert.Equal(targetCandle.Timestamp, result.SnappedChartPoint.Time);
        }

        [Fact]
        public void GetMagnetSnap_ShouldNotSnap_WhenFarAway()
        {
            // Arrange
            var now = DateTime.Now;
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < 100; i++)
            {
                candles.Add(new CoreCandleData(now.AddMinutes(i), 100, 110, 90, 105, 1000));
            }

            var transform = new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);
            transform.SetTimeRange(candles[0].Timestamp, candles[99].Timestamp);
            transform.SetPriceRange(80, 120);

            var service = new MagnetSnapService();

            // Act
            // Mouse far away from any candle
            var mousePoint = new global::Avalonia.Point(0, 0); // Top left (Price 120, Time Start)
            // Candle 0 is at (0, ~screenY for 105).
            // Price 120 is at screen Y=0.
            // Candle 0 Close is 105. 
            // 120 - 105 = 15 price units.
            // ScaleY = 500 / 40 = 12.5 px/unit.
            // Distance = 15 * 12.5 = 187.5 px.
            
            var result = service.GetMagnetSnap(mousePoint, candles, transform);

            // Assert
            Assert.False(result.IsSnapped, "Should not be snapped");
        }
    }
}
