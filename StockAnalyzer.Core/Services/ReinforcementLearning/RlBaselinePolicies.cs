using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Causal registered SMA and declared direction-only baseline policies.</summary>
public static class RlBaselinePolicies
{
    public static ImmutableArray<string> EvaluationPolicyIds(bool continuous)
    {
        var policies = ImmutableArray.Create(RlContract.SmaBaselineId, RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.RandomBaselineId);
        return continuous ? policies.Add(RlContinuousContract.PairedBaselineId) : policies;
    }
    public static ImmutableArray<RlAction> CreateSmaSignals(RlSourceSnapshot source) => CreateSmaSignals(source.Bars);

    /// <summary>
    /// The registered-SMA crossover signal of every bar. The signal at bar j uses only the closes j-<see cref="RlContract.SmaSlowPeriod"/>..j, so a window
    /// that starts at least that many bars before its first decision bar gives the same signals as the full history.
    /// </summary>
    public static ImmutableArray<RlAction> CreateSmaSignals(ImmutableArray<CandleData> bars)
    {
        var closes = bars.Select(bar => (decimal?)bar.Close).ToArray();
        var fast = IndicatorFactory.Default.Create(IndicatorType.SMA, new CoreSmaParameter { Period = RlContract.SmaFastPeriod })
            ?? throw new InvalidOperationException("Registered SMA is required.");
        var slow = IndicatorFactory.Default.Create(IndicatorType.SMA, new CoreSmaParameter { Period = RlContract.SmaSlowPeriod })
            ?? throw new InvalidOperationException("Registered SMA is required.");
        fast.CalculateSeries(closes); slow.CalculateSeries(closes);
        var actions = ImmutableArray.CreateBuilder<RlAction>(closes.Length);
        for (int i = 0; i < closes.Length; i++)
        {
            var action = RlAction.Hold;
            if (i >= RlContract.SmaSlowPeriod && fast.Values[i - 1] is { } previousFast && slow.Values[i - 1] is { } previousSlow
                && fast.Values[i] is { } currentFast && slow.Values[i] is { } currentSlow)
            {
                if (previousFast <= previousSlow && currentFast > currentSlow) action = RlAction.Buy;
                else if (previousFast >= previousSlow && currentFast < currentSlow) action = RlAction.Sell;
            }
            actions.Add(action);
        }
        return actions.MoveToImmutable();
    }

    public static RlAction GetAction(string policyId, RlSourceSnapshot source, RlEpisodeBlock block, int decision, int seed)
    {
        if (decision < 0 || decision >= RlContract.EpisodeHorizon) throw new ArgumentOutOfRangeException(nameof(decision));
        return policyId switch
        {
            RlContract.SmaBaselineId => CreateSmaSignals(source)[block.StartIndex + RlContract.WindowLength - 1 + decision],
            _ => GetAction(policyId, default, 0, decision, seed),
        };
    }

    /// <summary>
    /// The action of a baseline policy on an episode whose bars were cut from a longer history. <paramref name="smaSignals"/> are the signals of the
    /// episode's own bars (<see cref="CreateSmaSignals(ImmutableArray{CandleData})"/>) and <paramref name="barIndex"/> is the episode index of the
    /// first decision bar; both are used by the SMA policy only.
    /// </summary>
    public static RlAction GetAction(string policyId, ImmutableArray<RlAction> smaSignals, int barIndex, int decision, int seed)
    {
        if (decision < 0 || decision >= RlContract.EpisodeHorizon) throw new ArgumentOutOfRangeException(nameof(decision));
        return policyId switch
        {
            RlContract.SmaBaselineId => smaSignals.IsDefault ? throw new ArgumentException("SMA signals are required.", nameof(smaSignals))
                : smaSignals[barIndex + decision],
            RlContract.HoldBaselineId => RlAction.Hold,
            RlContract.BuyHoldBaselineId => decision == 0 ? RlAction.Buy : RlAction.Hold,
            RlContract.RandomBaselineId => RandomAction(seed, decision),
            _ => throw new ArgumentException("Unknown baseline policy.", nameof(policyId)),
        };
    }

    public static RlAction RandomAction(int seed, int decision)
    {
        var random = new Random(checked(seed + RlContract.RandomBaselineSeedOffset));
        int action = 0;
        for (int i = 0; i <= decision; i++) action = random.Next(RlContract.ActionCount);
        return (RlAction)action;
    }
}
