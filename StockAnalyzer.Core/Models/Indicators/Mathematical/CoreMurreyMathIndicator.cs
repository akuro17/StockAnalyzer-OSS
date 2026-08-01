using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Mathematical;

[StockAnalyzerIndicator(IndicatorType.MurreyMath)]
public class CoreMurreyMathIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 64;

    public override string Name => $"Murrey Math ({Period})";
    public override bool IsOverlay => true;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    private readonly List<decimal?> _level0 = new();
    private readonly List<decimal?> _level1 = new();
    private readonly List<decimal?> _level2 = new();
    private readonly List<decimal?> _level3 = new();
    private readonly List<decimal?> _level4 = new();
    private readonly List<decimal?> _level5 = new();
    private readonly List<decimal?> _level6 = new();
    private readonly List<decimal?> _level7 = new();
    private readonly List<decimal?> _level8 = new();

    public IReadOnlyList<decimal?> Level0_8 => _level0;
    public IReadOnlyList<decimal?> Level1_8 => _level1;
    public IReadOnlyList<decimal?> Level2_8 => _level2;
    public IReadOnlyList<decimal?> Level3_8 => _level3;
    public IReadOnlyList<decimal?> Level4_8 => _level4;
    public IReadOnlyList<decimal?> Level5_8 => _level5;
    public IReadOnlyList<decimal?> Level6_8 => _level6;
    public IReadOnlyList<decimal?> Level7_8 => _level7;
    public IReadOnlyList<decimal?> Level8_8 => _level8;

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        _level0.Clear();
        _level1.Clear();
        _level2.Clear();
        _level3.Clear();
        _level4.Clear();
        _level5.Clear();
        _level6.Clear();
        _level7.Clear();
        _level8.Clear();

        var candleDataList = candles.ToList();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                AddNulls(); // Adds nulls to level lists
                continue;
            }

            var window = candles.Skip(i - Period + 1).Take(Period).ToList();
            var highestHigh = window.Max(c => c.High);
            var lowestLow = window.Min(c => c.Low);

            var range = highestHigh - lowestLow;
            var interval = range / 8;

            var l4 = lowestLow + interval * 4;

            _level0.Add(lowestLow);
            _level1.Add(lowestLow + interval * 1);
            _level2.Add(lowestLow + interval * 2);
            _level3.Add(lowestLow + interval * 3);
            _level4.Add(l4);
            _level5.Add(lowestLow + interval * 5);
            _level6.Add(lowestLow + interval * 6);
            _level7.Add(lowestLow + interval * 7);
            _level8.Add(highestHigh);
            _values.Add(l4);
        }

        return IndicatorResult.Success(_values);
    }

    private void AddNulls()
    {
        _level0.Add(null);
        _level1.Add(null);
        _level2.Add(null);
        _level3.Add(null);
        _level4.Add(null);
        _level5.Add(null);
        _level6.Add(null);
        _level7.Add(null);
        _level8.Add(null);
    }
}
