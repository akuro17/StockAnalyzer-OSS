using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

public enum BacktestStrategyKind
{
    NoOp = 0,
    ConditionBased = 1,
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

    public BacktestStrategyKind Kind { get; }
    public ImmutableArray<StrategyIndicatorRequest> IndicatorRequests => CloneRequests(_indicatorRequests);
    public ImmutableArray<BacktestConditionEntry> Conditions => BacktestConditionValidator.Snapshot(_conditions);
    public BacktestRiskManagementSettings? RiskManagement => CloneRisk(_riskManagement);

    private BacktestStrategySpecification(
        BacktestStrategyKind kind,
        ImmutableArray<StrategyIndicatorRequest> indicatorRequests,
        ImmutableArray<BacktestConditionEntry> conditions,
        BacktestRiskManagementSettings? riskManagement)
    {
        Kind = kind;
        _indicatorRequests = indicatorRequests;
        _conditions = conditions;
        _riskManagement = riskManagement;
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

    internal IBacktestStrategy CreateStrategy() => Kind switch
    {
        BacktestStrategyKind.NoOp => new NoOpBacktestStrategy(CloneRequests(_indicatorRequests)),
        BacktestStrategyKind.ConditionBased => new ConditionBasedBacktestStrategy(
            BacktestConditionValidator.Snapshot(_conditions),
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
