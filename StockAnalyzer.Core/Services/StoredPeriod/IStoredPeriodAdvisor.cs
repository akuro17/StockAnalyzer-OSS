using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.StoredPeriod;

/// <summary>
/// Proposes the pre-filled start and end date of the Training Wizard, the RL window and the Portfolio RL window from the stored bar dates (read-only; only
/// the date column is read, never a price). The end of a proposal is the latest stored date; the start is the date from which the required number of bars is
/// available. A typed end is honoured through <c>endBoundInclusive</c>.
/// </summary>
public interface IStoredPeriodAdvisor
{
    /// <summary>One RL symbol: stored daily bars that are final (before the exchange date of now) and equity metadata, as the RL start gate reads them.</summary>
    Task<StoredPeriodProposal> ProposeForRlAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken);

    /// <summary>The newest final stored date over the symbols (RL / Portfolio RL family), or null for no usable symbol or more symbols than the cap.</summary>
    Task<DateOnly?> LatestFinalizedDateAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken);

    /// <summary>The Training Wizard family: the configured folder of the timeframe, every stored bar counts (no finality rule).</summary>
    Task<StoredPeriodProposal> ProposeForTrainingAsync(IReadOnlyList<string> symbols, TrainingTimeframe timeframe, int requiredBars,
        DateOnly? endBoundInclusive, CancellationToken cancellationToken);

    /// <summary>
    /// The Training Wizard family: how many stored bars each symbol has in the period [<paramref name="start"/>, <paramref name="end"/>] (a null bound is
    /// unbounded) and from where <paramref name="requiredBars"/> bars are available (see <see cref="SymbolBarInfo"/>). Read-only; a symbol whose file cannot
    /// be read is left out. The default implementation reports nothing, so implementers written before this member keep working.
    /// </summary>
    Task<IReadOnlyList<SymbolBarInfo>> ReadSymbolBarsForTrainingAsync(IReadOnlyList<string> symbols, TrainingTimeframe timeframe, int requiredBars,
        DateOnly? start, DateOnly? end, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SymbolBarInfo>>(Array.Empty<SymbolBarInfo>());
}
