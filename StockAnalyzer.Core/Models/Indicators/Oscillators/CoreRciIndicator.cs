using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.RCI)]
public class CoreRciIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 9;
    public override string Name => $"RCI ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count < Period)
        {
            if (candles != null) _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        // Time ranks are constant for a fixed period: 1, 2, 3, ..., period
        var timeRanks = Enumerable.Range(1, Period).ToList();

        for (int i = Period - 1; i < candles.Count; i++)
        {
            var window = candles.Skip(i - Period + 1).Take(Period).ToList();

            // Get price ranks
            var priceRanks = window
                .Select((c, index) => new { Price = c.Close, OriginalIndex = index })
                .OrderBy(x => x.Price)
                .Select((x, rankIndex) => new { x.OriginalIndex, Rank = rankIndex + 1 })
                .OrderBy(x => x.OriginalIndex)
                .Select(x => x.Rank)
                .ToList();

            // Calculate the sum of squared differences of ranks
            decimal dSum = 0;
            for (int j = 0; j < Period; j++)
            {
                var diff = timeRanks[j] - priceRanks[j];
                dSum += diff * diff;
            }

            // Spearman's rank correlation formula
            decimal rci = (1m - (6m * dSum) / (Period * (Period * Period - 1m))) * 100m;
            _values.Add(rci);
        }

        return IndicatorResult.Success(_values);
    }
}
