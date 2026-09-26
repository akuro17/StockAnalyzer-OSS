using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>L2 indicator warm-up with a hand-computable golden: the REAL SMA on artificial data.</summary>
[Trait("Category", "BacktestVerification")]
public class L2_IndicatorWarmupGoldenTests
{
    [Fact]
    public void Sma3_OverTenTwentyThirtyFortyFifty_IsNullNullThenTwentyThirtyForty()
    {
        var candles = SyntheticBars.FlatSeries(10m, 20m, 30m, 40m, 50m)
            .Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume))
            .ToArray();

        ICoreIndicator indicator = new IndicatorFactory().Create(IndicatorType.SMA, new CoreSmaParameter { Period = 3 })!;
        IReadOnlyList<decimal?> values = indicator.Calculate(candles).MainValues;

        Assert.Equal(new decimal?[] { null, null, 20m, 30m, 40m }, values.ToArray());
    }

    [Fact]
    public void Sma5_FirstValueAppearsExactlyAtIndexPeriodMinusOne()
    {
        var candles = SyntheticBars.Ramp(20, 100m, 1m);
        var core = candles.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

        ICoreIndicator indicator = new IndicatorFactory().Create(IndicatorType.SMA, new CoreSmaParameter { Period = VerificationParameters.SmaPeriod })!;
        IReadOnlyList<decimal?> values = indicator.Calculate(core).MainValues;

        Assert.All(values.Take(VerificationParameters.SmaPeriod - 1), v => Assert.Null(v));
        Assert.NotNull(values[VerificationParameters.SmaPeriod - 1]);
        // Ramp Close = 100..104 over the first five bars => mean 102.
        Assert.Equal(102m, values[VerificationParameters.SmaPeriod - 1]);
    }
}
