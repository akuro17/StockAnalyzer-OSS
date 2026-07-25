using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Strategies
{
    public interface IChartStrategyFactory
    {
        IChartStrategy GetStrategy(ChartType chartType);
    }

    public class ChartStrategyFactory : IChartStrategyFactory
    {
        private readonly IEnumerable<IChartStrategy> _strategies;

        public ChartStrategyFactory(IEnumerable<IChartStrategy> strategies)
        {
            _strategies = strategies;
        }

        public IChartStrategy GetStrategy(ChartType chartType)
        {
            return _strategies.FirstOrDefault(s => s.TargetType == chartType)!;
        }
    }
}
