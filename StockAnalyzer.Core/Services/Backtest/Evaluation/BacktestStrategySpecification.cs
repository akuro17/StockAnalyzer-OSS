using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

public enum BacktestStrategyKind
{
    NoOp = 0,
    ConditionBased = 1,

    /// <summary>Appended value (existing numeric values stay stable): a <see cref="ConditionBasedBacktestStrategy"/> driven by an AND/OR condition tree.</summary>
    ConditionTree = 2,
}

/// <summary>
/// Immutable description of the built-in strategies accepted by the owned evaluation boundary.
/// It deliberately cannot describe an arbitrary <see cref="IBacktestStrategy"/> implementation.
/// </summary>
public sealed class BacktestStrategySpecification
{
    private readonly ImmutableArray<StrategyIndicatorRequest> _indicatorRequests;
    private readonly ImmutableArray<BacktestConditionEntry> _conditions;
    private readonly BacktestRiskManagementSettings? _riskManagement;
    private readonly BacktestConditionTree? _tree;

    public BacktestStrategyKind Kind { get; }
    public ImmutableArray<StrategyIndicatorRequest> IndicatorRequests => CloneRequests(_indicatorRequests);
    public ImmutableArray<BacktestConditionEntry> Conditions => BacktestConditionValidator.Snapshot(_conditions);
    public BacktestRiskManagementSettings? RiskManagement => CloneRisk(_riskManagement);

    /// <summary>An independent copy of the condition tree of a <see cref="BacktestStrategyKind.ConditionTree"/> specification; null for the other kinds.</summary>
    public BacktestConditionTree? Tree => _tree is null ? null : BacktestConditionValidator.SnapshotTree(_tree, maxOffset: null, maxDepth: null, maxNodes: null);

    private BacktestStrategySpecification(
        BacktestStrategyKind kind,
        ImmutableArray<StrategyIndicatorRequest> indicatorRequests,
        ImmutableArray<BacktestConditionEntry> conditions,
        BacktestRiskManagementSettings? riskManagement,
        BacktestConditionTree? tree = null)
    {
        Kind = kind;
        _indicatorRequests = indicatorRequests;
        _conditions = conditions;
        _riskManagement = riskManagement;
        _tree = tree;
    }

    public static BacktestStrategySpecification NoOp(IEnumerable<StrategyIndicatorRequest> indicatorRequests)
    {
        ArgumentNullException.ThrowIfNull(indicatorRequests);
        ImmutableArray<StrategyIndicatorRequest> owned = CloneRequests(indicatorRequests.ToImmutableArray());
        return new BacktestStrategySpecification(
            BacktestStrategyKind.NoOp,
            owned,
            ImmutableArray<BacktestConditionEntry>.Empty,
            null);
    }

    public static BacktestStrategySpecification ConditionBased(
        IReadOnlyList<BacktestConditionEntry> conditions,
        BacktestRiskManagementSettings? riskManagement = null)
    {
        ImmutableArray<BacktestConditionEntry> owned = BacktestConditionValidator.Snapshot(conditions);
        BacktestConditionValidator.ValidateRiskManagement(riskManagement);
        BacktestRiskManagementSettings? ownedRisk = CloneRisk(riskManagement);
        return new BacktestStrategySpecification(
            BacktestStrategyKind.ConditionBased,
            ImmutableArray<StrategyIndicatorRequest>.Empty,
            owned,
            ownedRisk);
    }

    /// <summary>
    /// Specification of a condition-tree strategy (new factory; <see cref="ConditionBased"/> is unchanged). The tree is validated and deep-copied here,
    /// so later changes to the caller's tree cannot alter the specification.
    /// </summary>
    public static BacktestStrategySpecification ConditionTree(
        BacktestConditionTree tree,
        BacktestRiskManagementSettings? riskManagement = null)
    {
        BacktestConditionTree owned = BacktestConditionValidator.SnapshotTree(tree, maxOffset: null, maxDepth: null, maxNodes: null);
        BacktestConditionValidator.ValidateRiskManagement(riskManagement);
        return new BacktestStrategySpecification(
            BacktestStrategyKind.ConditionTree,
            ImmutableArray<StrategyIndicatorRequest>.Empty,
            ImmutableArray<BacktestConditionEntry>.Empty,
            CloneRisk(riskManagement),
            owned);
    }

    internal IBacktestStrategy CreateStrategy() => Kind switch
    {
        BacktestStrategyKind.NoOp => new NoOpBacktestStrategy(CloneRequests(_indicatorRequests)),
        BacktestStrategyKind.ConditionBased => new ConditionBasedBacktestStrategy(
            BacktestConditionValidator.Snapshot(_conditions),
            CloneRisk(_riskManagement)),
        BacktestStrategyKind.ConditionTree => ConditionBasedBacktestStrategy.FromTree(
            BacktestConditionValidator.SnapshotTree(_tree!, maxOffset: null, maxDepth: null, maxNodes: null),
            CloneRisk(_riskManagement)),
        _ => throw new ArgumentException($"Unsupported built-in strategy kind '{Kind}'.", nameof(Kind)),
    };

    public ImmutableArray<StrategyIndicatorRequest> GetRequiredIndicators()
        => CreateStrategy().GetRequiredIndicators()
            .Select(request => request with { Parameters = request.Parameters?.Clone() })
            .ToImmutableArray();

    private static ImmutableArray<StrategyIndicatorRequest> CloneRequests(
        ImmutableArray<StrategyIndicatorRequest> requests) => requests
        .Select(request => request with { Parameters = request.Parameters?.Clone() })
        .ToImmutableArray();

    private static BacktestRiskManagementSettings? CloneRisk(BacktestRiskManagementSettings? riskManagement) =>
        riskManagement is null
            ? null
            : new BacktestRiskManagementSettings
            {
                StopLossPercent = riskManagement.StopLossPercent,
                TakeProfitPercent = riskManagement.TakeProfitPercent,
            };
}
