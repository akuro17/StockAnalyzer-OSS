using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Which symbols of a target panel the base model of a transfer may already have seen, from the two immutable panel manifests (no new file, no schema
/// change, never a rejection: the same symbols in a later period is a legitimate use). The base training group is the base panel's symbols minus its
/// held-out symbols; symbols that were only evaluated (never trained on) are included, so every count is an UPPER BOUND of what the base was trained on.
/// All lists are sorted by ordinal comparison; symbols are compared case-sensitively.
/// </summary>
/// <param name="BaseTrainingGroupSymbols">Symbols of the base panel that were not held out there.</param>
/// <param name="TargetHeldOutSymbols">Symbols held out in the target panel.</param>
/// <param name="HeldOutSeenByBase">Target held-out symbols that are in the base training group: not unseen for this run.</param>
/// <param name="TargetSymbolsSeenByBase">All target symbols that are in the base training group.</param>
public sealed record RlPortfolioSymbolOverlap(ImmutableArray<string> BaseTrainingGroupSymbols, ImmutableArray<string> TargetHeldOutSymbols,
    ImmutableArray<string> HeldOutSeenByBase, ImmutableArray<string> TargetSymbolsSeenByBase)
{
    public static RlPortfolioSymbolOverlap Of(RlPanelManifest baseManifest, RlPanelManifest targetManifest)
    {
        ArgumentNullException.ThrowIfNull(baseManifest);
        ArgumentNullException.ThrowIfNull(targetManifest);
        var baseHeldOut = Names(baseManifest.HoldoutSymbols).ToHashSet(StringComparer.Ordinal);
        var baseTraining = Sorted(baseManifest.Symbols.IsDefault ? Enumerable.Empty<string>() : baseManifest.Symbols.Select(e => e.Symbol).Where(s => !baseHeldOut.Contains(s)));
        var seen = baseTraining.ToHashSet(StringComparer.Ordinal);
        var targetHeldOut = Sorted(Names(targetManifest.HoldoutSymbols));
        var targetAll = Sorted(targetManifest.Symbols.IsDefault ? Enumerable.Empty<string>() : targetManifest.Symbols.Select(e => e.Symbol));
        return new RlPortfolioSymbolOverlap(baseTraining, targetHeldOut, targetHeldOut.Where(seen.Contains).ToImmutableArray(),
            targetAll.Where(seen.Contains).ToImmutableArray());
    }

    private static IEnumerable<string> Names(ImmutableArray<string> symbols) => symbols.IsDefault ? Enumerable.Empty<string>() : symbols;

    private static ImmutableArray<string> Sorted(IEnumerable<string> symbols) => symbols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
}
