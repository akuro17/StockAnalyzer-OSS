using Xunit;
using Xunit.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Strategies;
using System;
using System.Collections.Generic;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Tests.Analysis
{
    public class KagiPipelineTests
    {
        private readonly ITestOutputHelper _output;

        public KagiPipelineTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Kagi_StrategyPipelineTest()
        {
            var data = new List<CoreCandleData>();
            var startDate = new DateTime(2025, 1, 1);
            
            void Add(decimal close)
            {
                data.Add(new CoreCandleData(startDate.AddDays(data.Count), close, close, close, close, 100));
            }

            Add(100); Add(105); Add(110); Add(120); 
            Add(115); Add(105); Add(108); Add(116); 
            Add(110); Add(95);  Add(90);

            // Simulate the ChartParameterBuilder creating parameters from ViewModel
            var parameters = new ChartStrategyParameters(
                Mode: ChartSizingMode.Fixed,
                ManualSize: 10m,
                AtrPeriod: 14,
                AtrMultiplier: 1m,
                Percentage: 1m
            );

            var strategy = new KagiChartStrategy();
            var result = strategy.Calculate(data, parameters);

            _output.WriteLine("---- PIPELINE OUTPUT ----");
            var adapter = result.Adapter as ChartSegmentAdapter;
            Assert.NotNull(adapter);

            var closes = adapter.Closes.Span;
            var opens = adapter.Opens.Span;
            var volumes = adapter.Volumes.Span;

            for (int i = 0; i < adapter.Count; i++)
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
