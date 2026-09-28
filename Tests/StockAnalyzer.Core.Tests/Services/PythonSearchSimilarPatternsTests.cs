using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>Drives the real server.py: the structural filter must use the volatility sent by .NET and must not need the arch package.</summary>
    [Collection("PythonIpc")]
    public class PythonSearchSimilarPatternsTests
    {
        private sealed class SearchSettings : PythonIpcTestSettingsBase
        {
            public override string PipeName => "testsearchsimilarpipe";
        }

        private static List<CandleData> Candles(int count)
        {
            var candles = new List<CandleData>(count);
            for (int i = 0; i < count; i++)
            {
                decimal close = 100m + (decimal)(8.0 * Math.Sin(i / 6.0)) + (decimal)(3.0 * Math.Sin(i / 2.3));
                candles.Add(new CandleData(new DateTime(2020, 1, 1).AddDays(i), close, close + 1m, close - 1m, close, 1000));
            }
            return candles;
        }

        private static async Task<JsonElement> SearchAsync(int count, bool useStructural, IReadOnlyList<double>? volatility)
        {
            var pythonService = new PythonService(new SearchSettings());
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();
            var candles = Candles(count);

            string json = await pythonService.ExecuteTransactionAsync(async () =>
            {
                await pythonService.SendCandlesAsync(candles);
                return await pythonService.SearchSimilarPatternsAsync(
                    lookback: 0, topK: 3, futureSteps: 10, threshold: 0.3, queryLength: 30, queryStartIndex: count - 30,
                    useStructural: useStructural, warpingRadius: 5, volatility: volatility);
            });

            return JsonDocument.Parse(json).RootElement.Clone();
        }

        [Fact]
        public async Task Search_WithoutStructural_ReturnsPatterns()
        {
            var root = await SearchAsync(300, useStructural: false, volatility: null);

            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("result").GetProperty("patterns").GetArrayLength() > 0);
        }

        [Fact]
        public async Task Search_StructuralWithSuppliedVolatility_SucceedsAndChangesTheProbabilities()
        {
            var plain = await SearchAsync(300, useStructural: false, volatility: null);
            // A volatility level that jumps inside the history makes every candidate away from the query's level pay a penalty.
            double[] volatility = Enumerable.Range(0, 300).Select(i => i < 150 ? 0.5 : 3.0).ToArray();
            var structural = await SearchAsync(300, useStructural: true, volatility: volatility);

            Assert.Equal("ok", structural.GetProperty("status").GetString());
            double plainTop = plain.GetProperty("result").GetProperty("patterns")[0].GetProperty("probability").GetDouble();
            var structuralPatterns = structural.GetProperty("result").GetProperty("patterns");
            Assert.True(structuralPatterns.GetArrayLength() > 0);
            Assert.True(structuralPatterns.EnumerateArray().Any(p => p.GetProperty("probability").GetDouble() < plainTop));
        }

        [Fact]
        public async Task Search_StructuralWithWrongLengthVolatility_FallsBackToUnfilteredSearch()
        {
            var plain = await SearchAsync(300, useStructural: false, volatility: null);
            var structural = await SearchAsync(300, useStructural: true, volatility: new double[] { 1.0, 2.0 });

            Assert.Equal("ok", structural.GetProperty("status").GetString());
            Assert.Equal(
                plain.GetProperty("result").GetProperty("patterns")[0].GetProperty("probability").GetDouble(),
                structural.GetProperty("result").GetProperty("patterns")[0].GetProperty("probability").GetDouble());
        }

        [Fact]
        public async Task Search_StructuralWithoutVolatility_FallsBackToUnfilteredSearch()
        {
            var structural = await SearchAsync(300, useStructural: true, volatility: null);

            Assert.Equal("ok", structural.GetProperty("status").GetString());
            Assert.True(structural.GetProperty("result").GetProperty("patterns").GetArrayLength() > 0);
        }
    }
}
