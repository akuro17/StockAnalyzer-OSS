using Xunit;
using Xunit.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ZeroAllocation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Tests.Analysis
{
    public class KagiCalculatorTests
    {
        private readonly ITestOutputHelper _output;

        public KagiCalculatorTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Kagi_StateTransitionTest()
        {
            var data = new List<ZeroAllocCandleData>();
            var startDate = new DateTime(2025, 1, 1);
            
            // Add helper to add data
            void Add(decimal close)
            {
                data.Add(new ZeroAllocCandleData(startDate.AddDays(data.Count), close, close, close, close, 100));
            }

            // Reversal Amount = 10
            Add(100);
            Add(105);
            Add(110);
            Add(120); // UP to 120
            Add(115);
            Add(105); // DOWN to 105 (Reversal)
            Add(108);
            Add(116); // UP to 116 (Reversal)
            Add(110);
            Add(95);  // DOWN to 95 (Reversal, crosses 100, should trigger Yang->Yin)
            Add(90);

            var parameters = new KagiParameters(10m);
            var result = KagiCalculator.Calculate(data.ToArray(), parameters);

            _output.WriteLine("---- KAGI OUTPUT ----");
            var closes = result.Closes.Span;
            var opens = result.Opens.Span;
            var volumes = result.Volumes.Span;

            for (int i = 0; i < result.Count; i++)
            {
                bool isUp = closes[i] >= opens[i];
                bool isYang = volumes[i] >= 0;
                string dir = isUp ? "UP" : "DOWN";
                string state = isYang ? "YANG(Green)" : "YIN(Red)";
                _output.WriteLine($"Segment {i}: Open={opens[i]}, Close={closes[i]}, {dir}, State={state}");
            }
        }
    }
}
