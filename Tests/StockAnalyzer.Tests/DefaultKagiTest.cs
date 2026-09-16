using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Tests
{
    public class DefaultKagiTest
    {
        [Fact]
        public void TestKagiGeneratorOutput()
        {
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(new DateTime(2023, 1, 1), 100, 100, 100, 100, 100),
                new CoreCandleData(new DateTime(2023, 1, 2), 100, 110, 110, 110, 100),
                new CoreCandleData(new DateTime(2023, 1, 3), 110, 120, 120, 120, 100),
                new CoreCandleData(new DateTime(2023, 1, 4), 120, 120, 115, 115, 100),
                new CoreCandleData(new DateTime(2023, 1, 5), 115, 115, 105, 105, 100),
                new CoreCandleData(new DateTime(2023, 1, 6), 105, 105, 95, 95, 100),
                new CoreCandleData(new DateTime(2023, 1, 7), 95, 95, 90, 90, 100),
            };

            var kagi = KagiConverter.Convert(candles, 3m);
            Assert.NotEmpty(kagi);
        }
    }
}
