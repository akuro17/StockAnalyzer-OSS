using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Tests.Visual
{
    public static class TestDataFactory
    {
        public static List<CandleData> CreateDeterministicCandles(int count)
        {
            var data = new List<CandleData>();
            var baseDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < count; i++)
            {
                decimal open = 1000m + (i * 10m);
                decimal close = (i % 2 == 0) ? open + 5m : open - 5m;
                decimal volume = 1000m + (i * 100m);
                
                data.Add(new CandleData
                {
                    Timestamp = baseDate.AddDays(i),
                    Open = open,
                    High = Math.Max(open, close) + 2m,
                    Low = Math.Min(open, close) - 2m,
                    Close = close,
                    Volume = (long)volume
                });
            }
            return data;
        }
    }
}
