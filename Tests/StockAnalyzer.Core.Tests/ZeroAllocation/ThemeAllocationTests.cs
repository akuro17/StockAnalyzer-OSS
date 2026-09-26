using System;
using SkiaSharp;
using StockAnalyzer.Core.Theme;
using Xunit;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.ZeroAllocation
{
    public class ThemeAllocationTests
    {
        [Fact]
        public void ThemeColors_StaticInstances_ShouldBeSame()
        {
            var dark1 = ThemeColors.Dark;
            var dark2 = ThemeColors.Dark;
            Assert.Same(dark1, dark2);
            
            var light1 = ThemeColors.Light;
            var light2 = ThemeColors.Light;
            Assert.Same(light1, light2);
        }

        [Fact]
        public void ThemeColors_PropertyAccess_ShouldNotAllocate()
        {
            // Warm-up to ensure any static constructor or internal JIT-related allocations are done
            var theme = ThemeColors.Dark;
            _ = theme.ChartBackground;
            _ = theme.GridLine;

            // Start monitoring allocations on the current thread
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            
            // Perform multiple accesses to various properties
            for (int i = 0; i < 100; i++)
            {
                var t = ThemeColors.Dark;
                _ = t.ChartBackground;
                _ = t.GridLine;
                _ = t.AxisText;
                _ = t.Crosshair;
                _ = t.Bullish;
                _ = t.Bearish;
                _ = t.IsDark;
            }

            long endBytes = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = endBytes - startBytes;

            // In a strict ZeroAllocation environment, accessing existing static readonly properties 
            // of a record/class should not result in any heap allocations.
            Assert.Equal(0, totalAllocated);
        }
        
        [Fact]
        public void ThemeColors_Light_Values_MatchStrictSpec()
        {
            var t = ThemeColors.Light;
            Assert.Equal(IndicatorColor.FromUInt(0xFFF0F0F0), t.ChartBackground);
            Assert.Equal(IndicatorColor.FromUInt(0xFF555555), t.AxisText);
        }

        [Fact]
        public void ThemeColors_Dark_Values_MatchStrictSpec()
        {
            var t = ThemeColors.Dark;
            Assert.Equal(IndicatorColor.FromUInt(0xFF181A20), t.ChartBackground);
            Assert.Equal(IndicatorColor.FromUInt(0xFF787B86), t.AxisText);
        }
    }
}
