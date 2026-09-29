using Xunit;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests.Verification;

public class AnalysisPipelineServiceTests
{
    [Fact]
    public void CalculateIndicators_ReturnsDictionaryWithResults()
    {
        var service = new AnalysisPipelineService();
        var candles = new List<CoreCandleData> 
        { 
            new CoreCandleData(DateTime.Now, 10, 10, 10, 10, 100) 
        };
        
        var settings = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings 
            { 
                // Id is read-only, usage auto-generated
                IsEnabled = true, 
                TypeEnum = IndicatorType.SMA,
                ParameterObject = new CoreSmaParameter { Period = 1 }
            }
        };

        var result = service.CalculateIndicators(candles, settings);
        var id = settings[0].Id;

        Assert.NotNull(result);
        Assert.True(result.ContainsKey(id));
        Assert.True(result[id].IsSuccessful);
        Assert.NotEmpty(result[id].MainValues); // Expect 1 value (10)
    }
}
