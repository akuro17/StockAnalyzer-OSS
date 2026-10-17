using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

[Trait("Category", "BacktestVerification")]
public class L2_MarketStructureShiftPrefixTests
{
    private readonly ITestOutputHelper _output;

    public L2_MarketStructureShiftPrefixTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    [Fact]
    public void MarketStructureShift_MeasuresPrefixCausality_AcrossScenarios()
    {
        var factory = new IndicatorFactory();
        int[] cuts = { 120, 160, 200, 240, 280, 320, 360 };
        decimal[] thresholds = { 5.0m, 2.0m };
        string[] seriesNames = { "BOS_Lines", "CHoCH_Lines" };

        int totalEvaluated = 0;
        int mismatchCount = 0;
        string? firstExample = null;

        for (int seed = 1; seed <= 20; seed++)
        {
            ImmutableArray<CandleData> allBars = SyntheticBars.SeededRandomWalk(seed, 400, VerificationParameters.FuzzStartPrice);
            CoreCandleData[] allCore = ToCore(allBars);

            foreach (decimal threshold in thresholds)
            {
                var fullIndicator = factory.Create(IndicatorType.MarketStructureShift)!;
                fullIndicator.Configure(new CoreMarketStructureShiftParameter { ZigZagThreshold = threshold });
                var fullResult = fullIndicator.Calculate(allCore);
                Assert.True(fullResult.IsSuccessful, "Full calculation must succeed");

                foreach (int t in cuts)
                {
                    CoreCandleData[] prefixCore = ToCore(allBars.Take(t));
                    var prefixIndicator = factory.Create(IndicatorType.MarketStructureShift)!;
                    prefixIndicator.Configure(new CoreMarketStructureShiftParameter { ZigZagThreshold = threshold });
                    var prefixResult = prefixIndicator.Calculate(prefixCore);
                    Assert.True(prefixResult.IsSuccessful, "Prefix calculation must succeed");

                    totalEvaluated++;
                    bool scenarioHasMismatch = false;

                    foreach (string seriesName in seriesNames)
                    {
                        var fullSeries = fullResult.GetSeries(seriesName);
                        var prefixSeries = prefixResult.GetSeries(seriesName);

                        for (int i = 0; i < t; i++)
                        {
                            if (fullSeries[i] != prefixSeries[i])
                            {
                                scenarioHasMismatch = true;
                                if (firstExample == null)
                                {
                                    firstExample = $"seed={seed}, t={t}, threshold={threshold}, series={seriesName}, bar={i}, prefix={prefixSeries[i]}, full={fullSeries[i]}";
                                }
                                break;
                            }
                        }
                    }

                    if (scenarioHasMismatch)
                    {
                        mismatchCount++;
                    }
                }
            }
        }

        _output.WriteLine($"Total scenarios: {totalEvaluated}, Mismatches: {mismatchCount}");
        if (firstExample != null)
        {
            _output.WriteLine($"First mismatch example: {firstExample}");
        }

        // Output results to diagnostic
        Assert.Equal(280, totalEvaluated);
        // We assert that the probe detects the non-causal look-ahead defect (at least one mismatch).
        Assert.True(mismatchCount > 0, "Expected MarketStructureShift to be non-causal (at least 1 mismatch)");
    }
}
