using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Watchlist;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Resolved ticker symbols and metadata for a ticker group node.
/// </summary>
public sealed record GroupMembers(
    IReadOnlyList<string> Symbols,
    int UnregisteredCount,
    IReadOnlyList<string> UnknownRuleFields,
    TickerGroupKind Kind);

/// <summary>
/// Interface for resolving members of static or filter ticker groups.
/// </summary>
public interface ITickerGroupResolver
{
    Task<GroupMembers> ResolveAsync(Guid groupId, int maxFilterEvaluationSymbols, CancellationToken cancellationToken = default);
}

/// <summary>
/// Core resolver for ticker groups. Evaluates static group memberships and recursive filter node rules.
/// </summary>
public sealed class TickerGroupResolver : ITickerGroupResolver
{
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly IReadOnlyWorkspaceSource _workspaceSource;

    public TickerGroupResolver(IReadOnlyStockAnalyzerGateway gateway, IReadOnlyWorkspaceSource workspaceSource)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _workspaceSource = workspaceSource ?? throw new ArgumentNullException(nameof(workspaceSource));
    }

    public async Task<GroupMembers> ResolveAsync(Guid groupId, int maxFilterEvaluationSymbols, CancellationToken cancellationToken = default)
    {
        if (maxFilterEvaluationSymbols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFilterEvaluationSymbols), "Limit must be positive.");
        }

        var workspaceSettings = await _workspaceSource.LoadAsync(cancellationToken).ConfigureAwait(false);
        var registry = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);

        var tree = TickerGroupTree.Build(workspaceSettings, registry, TickerGroupTree.DefaultMaxDepth);
        var nodeMap = tree.Nodes.ToDictionary(n => n.Id);

        if (!nodeMap.ContainsKey(groupId))
        {
            throw new GroupNotFoundException(groupId);
        }

        var registrySet = new HashSet<string>(registry, StringComparer.OrdinalIgnoreCase);
        var resolvedCache = new Dictionary<Guid, GroupMembers>();
        var visiting = new HashSet<Guid>();

        var evaluator = new TickerFilterEvaluator(_gateway);
        long cumulativeEvaluatedSymbols = 0;

        async Task<GroupMembers> ResolveNodeAsync(Guid id)
        {
            if (resolvedCache.TryGetValue(id, out var cached))
            {
                return cached;
            }

            if (!nodeMap.TryGetValue(id, out var node))
            {
                throw new GroupNotFoundException(id);
            }

            if (!visiting.Add(id))
            {
                throw new InvalidOperationException($"Cycle detected in ticker group tree at group '{id}'.");
            }

            try
            {
                if (node.Kind != TickerGroupKind.Filter)
                {
                    var rawMembers = node.Members ?? Array.Empty<string>();
                    var registered = rawMembers
                        .Where(registrySet.Contains)
                        .OrderBy(s => s, StringComparer.Ordinal)
                        .ToList();
                    int unregistered = rawMembers.Count - registered.Count;
                    var result = new GroupMembers(registered, unregistered, Array.Empty<string>(), node.Kind);
                    resolvedCache[id] = result;
                    return result;
                }
                else
                {
                    Guid parentId = node.ParentId ?? TickerGroupIds.AllTickers;
                    var parentGroup = await ResolveNodeAsync(parentId).ConfigureAwait(false);
                    var parentSymbols = parentGroup.Symbols;

                    var unknownRuleFields = CollectUnknownRuleFields(node.Rules);

                    // Cap check before reading per-symbol data (cumulative across all evaluated filter nodes)
                    long projectedEvaluations = cumulativeEvaluatedSymbols + parentSymbols.Count;
                    if (projectedEvaluations > maxFilterEvaluationSymbols)
                    {
                        int reportedCount = projectedEvaluations > int.MaxValue ? int.MaxValue : (int)projectedEvaluations;
                        throw new FilterEvaluationLimitException(reportedCount, maxFilterEvaluationSymbols);
                    }

                    if (parentSymbols.Count == 0)
                    {
                        var emptyResult = new GroupMembers(Array.Empty<string>(), 0, unknownRuleFields, TickerGroupKind.Filter);
                        resolvedCache[id] = emptyResult;
                        return emptyResult;
                    }

                    if (node.Rules == null || node.Rules.Count == 0)
                    {
                        var allParent = new GroupMembers(parentSymbols, 0, unknownRuleFields, TickerGroupKind.Filter);
                        resolvedCache[id] = allParent;
                        return allParent;
                    }

                    cumulativeEvaluatedSymbols = projectedEvaluations;

                    var matchedSorted = (await evaluator.FilterAsync(parentSymbols, node.Rules, cancellationToken).ConfigureAwait(false)).ToList();
                    var filterResult = new GroupMembers(matchedSorted, 0, unknownRuleFields, TickerGroupKind.Filter);
                    resolvedCache[id] = filterResult;
                    return filterResult;
                }
            }
            finally
            {
                visiting.Remove(id);
            }
        }

        return await ResolveNodeAsync(groupId).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> CollectUnknownRuleFields(IReadOnlyList<FilterRule>? rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return Array.Empty<string>();
        }

        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (!string.IsNullOrWhiteSpace(rule.Field) && !TickerRow.IsKnownField(rule.Field))
            {
                unknown.Add(rule.Field);
            }

            if (rule.IsCompareToField && !string.IsNullOrWhiteSpace(rule.Value) && !TickerRow.IsKnownField(rule.Value))
            {
                unknown.Add(rule.Value);
            }
        }

        return unknown.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
