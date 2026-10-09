using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// The one definition of the cell a portfolio run selects its checkpoint on: the validation partition of the training symbols. Its returns are
/// therefore optimistic; the trainer's feedback blocks, the selection evidence file and the result text all read this class. Deliberately not a
/// member of <see cref="RlPortfolioContract"/> (that would change the pinned contract hash).
/// </summary>
public static class RlPortfolioSelectionCell
{
    public const string Partition = RlPortfolioContract.ValidationPartition;
    public const RlPanelSymbolGroup Group = RlPanelSymbolGroup.Train;

    public static bool Is(string partition, RlPanelSymbolGroup group) => partition == Partition && group == Group;

    public static ImmutableArray<RlPanelBlock> Blocks(RlPanelCatalog catalog) => catalog.CellBlocks(Partition, Group);
}
