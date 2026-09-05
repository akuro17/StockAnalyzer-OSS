using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.PVT)]
    public class CorePvtIndicator : CoreVptIndicator
    {
        public override string Name => "PVT";
    }
}
