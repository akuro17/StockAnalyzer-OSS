using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Golden Master (snapshot) regression net: the complete order / fill / trade / equity / signal history of each hand-calculated
/// scenario is compared with a reviewed JSON file, so a change of even one minimal decimal unit or one trade is detected.
/// The explicit-assertion tests of this folder are the oracle; these files are the regression net around them.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class GoldenMasterTests
{
    public static IEnumerable<object[]> ScenarioNames() => VerificationScenarios.Golden.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Scenario_MatchesGoldenSnapshot(string scenarioName)
    {
        ScenarioRun run = VerificationScenarios.Golden[scenarioName]();
        GoldenMaster.Verify(scenarioName, run.Result);
    }

    [Fact]
    public void Snapshot_IsStableAcrossRepeatedSerialization_AndScaleOfDecimalsIsNormalized()
    {
        ScenarioRun run = VerificationScenarios.Golden["cost_slippage_roundtrip"]();
        string first = BacktestSnapshot.ToCanonicalJson(run.Result);
        string second = BacktestSnapshot.ToCanonicalJson(run.Result);
        Assert.Equal(first, second);
        Assert.Contains("\"Price\": 108.9,", first);      // 108.9 (not 108.90 / 108.900...)
        Assert.DoesNotContain("\r", first);
    }
}
