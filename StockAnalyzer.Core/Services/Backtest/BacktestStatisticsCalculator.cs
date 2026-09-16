using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Services.Backtest
{
    public class BacktestStatisticsCalculator
    {
        private readonly IPythonService _pythonService;

        public BacktestStatisticsCalculator(IPythonService pythonService)
        {
            _pythonService = pythonService;
        }

        public async Task<BacktestStatistics> CalculateAsync(IEnumerable<Trade> trades)
        {
            // Ensure Python process is started
            await _pythonService.InitializeExternalProcessAsync();

            var responseJson = await _pythonService.CalculateBacktestStatsAsync(trades);
            
            // In some cases, named pipes might return multiple JSON responses concatenated like {}{}. Extract the first one.
            var jsonToParse = responseJson;
            int firstBraceEnd = responseJson.IndexOf("}{");
            if (firstBraceEnd > 0)
            {
                jsonToParse = responseJson.Substring(0, firstBraceEnd + 1);
            }
            
            var response = JsonSerializer.Deserialize<JsonElement>(jsonToParse);

            if (response.GetProperty("status").GetString() == "error")
            {
                throw new Exception($"Python Error: {response.GetProperty("error").GetString()}");
            }

            var resultElement = response.GetProperty("result");
            return JsonSerializer.Deserialize<BacktestStatistics>(resultElement.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new BacktestStatistics();
        }
    }
}
