using System;
using System.Linq;
using StockAnalyzer.Core.Services.Backtest.Optimization;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Optimization;

/// <summary>
/// Plan 04 improvement (L3) and owner decision 2026-10-30: only a combination with enough in-sample trades can be the best one of a window, and the
/// status says why a window has no best combination.
/// </summary>
public sealed class WalkForwardWindowSelectorTests
{
    private static InSampleOutcome Run(int? trades, decimal? objective, bool noBars = false) => new(noBars, trades, objective);

    private static WalkForwardWindowSelection Select(bool minimize, int minTrades, params InSampleOutcome[] outcomes)
        => WalkForwardWindowSelector.Select(outcomes, minimize, minTrades);

    [Fact]
    public void TheBestEligibleCombinationWinsAndItsObjectiveIsReturned()
    {
        WalkForwardWindowSelection selection = Select(false, 1, Run(3, 1.0m), Run(5, 2.5m), Run(2, 2.0m));

        Assert.Equal(WalkForwardWindowStatus.Evaluated, selection.Status);
        Assert.Equal(1, selection.BestIndex);
        Assert.Equal(2.5m, selection.BestObjective);
    }

    [Fact]
    public void ACombinationWithoutEnoughTradesIsNeverChosenEvenWhenItsObjectiveIsBetter()
    {
        // Minimize: the zero-trade combination has totalReturn 0, which beats the real result of -0.2 unless it is left out.
        WalkForwardWindowSelection selection = Select(true, 1, Run(0, 0m), Run(4, 0.1m), Run(6, -0.2m));

        Assert.Equal(2, selection.BestIndex);
        Assert.Equal(-0.2m, selection.BestObjective);
    }

    [Fact]
    public void AMinimumOfZeroLetsACombinationThatNeverTradedBeChosen()
    {
        WalkForwardWindowSelection selection = Select(true, 0, Run(0, 0m), Run(4, 0.1m));

        Assert.Equal(0, selection.BestIndex);
        Assert.Equal(WalkForwardWindowStatus.Evaluated, selection.Status);
    }

    [Fact]
    public void TheTradeRequirementIsAtLeastNotMoreThan()
    {
        Assert.Equal(1, Select(false, 3, Run(2, 9m), Run(3, 1m)).BestIndex);
        Assert.Equal(WalkForwardWindowStatus.NoInSampleTrades, Select(false, 4, Run(2, 9m), Run(3, 1m)).Status);
    }

    [Fact]
    public void EqualObjectivesAreDecidedByTheLowerIndex()
    {
        Assert.Equal(0, Select(false, 1, Run(2, 1m), Run(2, 1m)).BestIndex);
        Assert.Equal(0, Select(true, 1, Run(2, 1m), Run(2, 1m)).BestIndex);
    }

    [Fact]
    public void WhenNoCombinationTradedEnoughTheStatusIsNoInSampleTradesAndThereIsNoBest()
    {
        WalkForwardWindowSelection selection = Select(false, 1, Run(0, 0m), Run(0, 0m), Run(null, null));

        Assert.Equal(WalkForwardWindowStatus.NoInSampleTrades, selection.Status);
        Assert.Equal(-1, selection.BestIndex);
        Assert.Null(selection.BestObjective);
    }

    [Fact]
    public void WhenSomeCombinationTradedButNoneHasAValidObjectiveTheStatusIsNoValidInSampleObjective()
    {
        WalkForwardWindowSelection selection = Select(false, 1, Run(3, null), Run(0, 0m));

        Assert.Equal(WalkForwardWindowStatus.NoValidInSampleObjective, selection.Status);
        Assert.Equal(-1, selection.BestIndex);
    }

    [Fact]
    public void WhenNoRunEvaluatedABarTheStatusIsNoInSampleBarsWhateverTheTradeCounts()
    {
        WalkForwardWindowSelection selection = Select(false, 1, Run(5, 2m, noBars: true), Run(7, 3m, noBars: true));

        Assert.Equal(WalkForwardWindowStatus.NoInSampleBars, selection.Status);
        Assert.Equal(-1, selection.BestIndex);
    }

    [Fact]
    public void ARunWithBarsKeepsTheWindowEvaluatedEvenWhenOthersEvaluatedNoBar()
    {
        WalkForwardWindowSelection selection = Select(false, 1, Run(0, null, noBars: true), Run(2, 1m));

        Assert.Equal(WalkForwardWindowStatus.Evaluated, selection.Status);
        Assert.Equal(1, selection.BestIndex);
    }

    [Fact]
    public void AnEmptyListHasNoBarsAndNoBest()
    {
        // No outcome evaluated a bar: All() over an empty list is true, so the window is reported as having no in-sample bars.
        WalkForwardWindowSelection selection = WalkForwardWindowSelector.Select(Array.Empty<InSampleOutcome>(), false, 1);

        Assert.Equal(WalkForwardWindowStatus.NoInSampleBars, selection.Status);
        Assert.Equal(-1, selection.BestIndex);
    }

    [Fact]
    public void TheStatusNamesAreTheTextsTheToolWrites()
    {
        Assert.Equal(
            new[] { "Evaluated", "NoInSampleBars", "NoInSampleTrades", "NoValidInSampleObjective", "NoOutOfSampleBars" },
            Enum.GetNames<WalkForwardWindowStatus>());
    }
}
