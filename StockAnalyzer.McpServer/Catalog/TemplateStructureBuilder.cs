using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// How new conditions combine with an existing template when updating or extending it.
/// </summary>
internal enum TemplateMergeMode
{
    AppendAnd,
    AppendOr,
    Replace,
}

/// <summary>
/// The result of building or merging a template in memory.
/// </summary>
internal sealed record BuiltTemplateResult(
    string TemplateType,
    Guid Id,
    string Name,
    string? Description,
    int SchemaVersion,
    Guid? BaseTemplateId,
    string MergeMode,
    string SuggestedRelativePath,
    int NodeCount,
    int Depth,
    IReadOnlyList<string> Warnings,
    JsonElement TemplateElement,
    string TemplateJson,
    JsonNode? ConditionTree,
    JsonNode? Conditions,
    JsonObject? RiskManagement);

/// <summary>
/// Builds in-memory StockAnalyzer domain templates (ScreenerIndicatorTemplate and BacktestConditionTemplate) from MCP
/// condition JSON or merges new conditions into an existing saved template, producing the exact persisted JSON structure
/// while enforcing domain invariants and zero-mutation.
/// </summary>
internal sealed class TemplateStructureBuilder
{
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ScreenerConditionBinder _screenerBinder;
    private readonly BacktestConditionBinder _backtestBinder;
    private readonly ScreenerTemplateAdvisor _screenerAdvisor;
    private readonly TemplateConditionWriter _templateWriter;
    private readonly IStockAnalyzerSettings _settings;

    public TemplateStructureBuilder(
        IReadOnlyStockAnalyzerGateway gateway,
        ScreenerConditionBinder screenerBinder,
        BacktestConditionBinder backtestBinder,
        ScreenerTemplateAdvisor screenerAdvisor,
        TemplateConditionWriter templateWriter,
        IStockAnalyzerSettings settings)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _screenerBinder = screenerBinder ?? throw new ArgumentNullException(nameof(screenerBinder));
        _backtestBinder = backtestBinder ?? throw new ArgumentNullException(nameof(backtestBinder));
        _screenerAdvisor = screenerAdvisor ?? throw new ArgumentNullException(nameof(screenerAdvisor));
        _templateWriter = templateWriter ?? throw new ArgumentNullException(nameof(templateWriter));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<BuiltTemplateResult> BuildScreenerTemplateAsync(
        JsonElement arguments,
        TemplateMergeMode mergeMode,
        string mergeModeStr,
        CancellationToken cancellationToken)
    {
        if (arguments.TryGetProperty("conditions", out _))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'conditions' is for templateType 'backtestCondition'; give 'conditionTree' for 'screener'.", "conditions");
        }

        if (arguments.TryGetProperty("riskManagement", out _))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'riskManagement' is only supported for templateType 'backtestCondition'.", "riskManagement");
        }

        if (!arguments.TryGetProperty("conditionTree", out JsonElement treeElement) || treeElement.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'conditionTree' is required when templateType is 'screener'.", "conditionTree");
        }

        TimeFrame timeFrame = arguments.TryGetProperty("timeFrame", out _)
            ? ToolArgs.SupportedTimeFrame(arguments, "timeFrame")
            : TimeFrame.D1;

        BoundScreenerCondition bound = _screenerBinder.Bind(treeElement, "conditionTree");
        _screenerBinder.ApplyLiteralFrames(bound, timeFrame);

        List<ScreenerIndicatorEntry> newEntries = FlattenScreenerNode(bound.Condition.Root);
        if (newEntries.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "conditionTree must contain at least one comparison leaf.", "conditionTree");
        }

        Guid? baseId = ParseOptionalGuid(arguments, "baseTemplateId");
        ScreenerIndicatorTemplate? baseTemplate = null;
        if (baseId is { } baseGuid)
        {
            baseTemplate = await _gateway.GetScreenerTemplateAsync(baseGuid, cancellationToken).ConfigureAwait(false)
                ?? throw new ToolException(ToolErrorCodes.TemplateNotFound, $"No Screener template with id '{baseGuid}' was found.", "baseTemplateId");
        }
        else if (mergeMode == TemplateMergeMode.AppendOr)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "mergeMode 'appendOr' requires baseTemplateId.", "mergeMode");
        }

        List<ScreenerIndicatorEntry> combinedEntries;
        if (baseTemplate is not null)
        {
            if (mergeMode == TemplateMergeMode.Replace)
            {
                combinedEntries = newEntries;
            }
            else
            {
                LogicalOperator connectOp = mergeMode == TemplateMergeMode.AppendOr ? LogicalOperator.Or : LogicalOperator.And;
                bool hasEnabledBase = baseTemplate.Entries.Any(e => e.IsEnabled);
                if (hasEnabledBase && newEntries.Count > 1)
                {
                    for (int k = 0; k < newEntries.Count - 1; k++)
                    {
                        if (newEntries[k].LogicalOperator != connectOp)
                        {
                            string opName = newEntries[k].LogicalOperator.ToString();
                            throw new ToolException(
                                ToolErrorCodes.InvalidArgument,
                                $"Cannot merge a multi-entry conditionTree containing '{opName}' into a non-empty base Screener template using '{mergeModeStr}' without changing left-to-right evaluation precedence.",
                                "conditionTree");
                        }
                    }
                }

                combinedEntries = baseTemplate.Entries.Select(e => e.Clone()).ToList();
                int lastEnabledIdx = combinedEntries.FindLastIndex(e => e.IsEnabled);
                if (lastEnabledIdx >= 0)
                {
                    combinedEntries[lastEnabledIdx].LogicalOperator = connectOp;
                }

                combinedEntries.AddRange(newEntries);
            }
        }
        else
        {
            combinedEntries = newEntries;
        }

        int maxLeaves = _settings.ScreenerMaxFilterTreeNodes;
        int enabledCount = combinedEntries.Count(e => e.IsEnabled);
        if (enabledCount > maxLeaves)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"The combined Screener template has {enabledCount} enabled entries, exceeding the maximum of {maxLeaves}.",
                "conditionTree");
        }

        for (int i = 0; i < combinedEntries.Count; i++)
        {
            combinedEntries[i].Label = FormatEntryLabel(i);
        }

        string name = ResolveName(arguments, baseTemplate);
        string? description = ResolveDescription(arguments, baseTemplate);
        Guid templateId = ResolveTemplateId(arguments, baseTemplate);
        DateTime nowUtc = DateTime.UtcNow;

        var template = new ScreenerIndicatorTemplate
        {
            Id = templateId,
            Name = name,
            Description = description,
            SchemaVersion = ScreenerIndicatorTemplate.CurrentSchemaVersion,
            CreatedAt = baseTemplate?.CreatedAt ?? nowUtc,
            UpdatedAt = nowUtc,
            IsFavorite = baseTemplate?.IsFavorite ?? false,
        };
        template.SetEntries(combinedEntries);

        IReadOnlyList<string> warnings = _screenerAdvisor.Warnings(template);
        ScreenerTemplateConversion conversion = _templateWriter.WriteScreenerTemplate(template);

        string templateJson = JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions());
        using var doc = JsonDocument.Parse(templateJson);
        JsonElement templateElement = doc.RootElement.Clone();

        string relativePath = $"Data/Templates/Screener/{templateId}.json";
        return new BuiltTemplateResult(
            TemplateType: "screener",
            Id: templateId,
            Name: name,
            Description: description,
            SchemaVersion: template.SchemaVersion,
            BaseTemplateId: baseId,
            MergeMode: mergeModeStr,
            SuggestedRelativePath: relativePath,
            NodeCount: combinedEntries.Count,
            Depth: combinedEntries.Count > 0 ? 1 : 0,
            Warnings: warnings,
            TemplateElement: templateElement,
            TemplateJson: templateJson,
            ConditionTree: conversion.ConditionTree,
            Conditions: null,
            RiskManagement: null);
    }

    public async Task<BuiltTemplateResult> BuildBacktestTemplateAsync(
        JsonElement arguments,
        TemplateMergeMode mergeMode,
        string mergeModeStr,
        CancellationToken cancellationToken)
    {
        if (arguments.TryGetProperty("conditionTree", out _))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'conditionTree' is for templateType 'screener'; give 'conditions' for 'backtestCondition'.", "conditionTree");
        }

        TimeFrame timeFrame = arguments.TryGetProperty("timeFrame", out _)
            ? ToolArgs.SupportedTimeFrame(arguments, "timeFrame")
            : TimeFrame.D1;

        if (!ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Contains(timeFrame))
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                $"Time frame '{timeFrame}' is not supported for backtest templates. Supported: {string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}.",
                "timeFrame");
        }

        Guid? baseId = ParseOptionalGuid(arguments, "baseTemplateId");
        BacktestConditionTemplate? baseTemplate = null;
        if (baseId is { } baseGuid)
        {
            baseTemplate = await _gateway.GetBacktestConditionTemplateAsync(baseGuid, cancellationToken).ConfigureAwait(false)
                ?? throw new ToolException(ToolErrorCodes.TemplateNotFound, $"No backtest-condition template with id '{baseGuid}' was found.", "baseTemplateId");
        }
        else if (mergeMode == TemplateMergeMode.AppendOr)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "mergeMode 'appendOr' requires baseTemplateId.", "mergeMode");
        }

        bool hasConditions = arguments.TryGetProperty("conditions", out JsonElement conditionsElement) && conditionsElement.ValueKind == JsonValueKind.Object;
        bool hasRisk = arguments.TryGetProperty("riskManagement", out JsonElement riskElement);

        if (baseTemplate is null && !hasConditions)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'conditions' is required when creating a new backtest-condition template.", "conditions");
        }

        if (baseTemplate is not null && !hasConditions && !hasRisk)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "Give at least one of 'conditions' or 'riskManagement' when updating a baseTemplateId.", "conditions");
        }

        BoundBacktestConditions? incomingBound = null;
        if (hasConditions)
        {
            incomingBound = _backtestBinder.Bind(conditionsElement, "conditions", timeFrame);
        }

        BoundBacktestConditions? baseBound = null;
        if (baseTemplate is not null)
        {
            baseBound = _backtestBinder.FromTemplate(baseTemplate, "baseTemplateId", timeFrame);
        }

        BacktestConditionTree mergedTree = BacktestConditionTree.Create((section, side) =>
        {
            BacktestConditionGroup? b = baseBound?.Tree.Root(section, side);
            BacktestConditionGroup? n = incomingBound?.Tree.Root(section, side);
            return MergeBacktestRoot(b, n, mergeMode);
        });

        if (!mergedTree.HasAnyLeaf)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "The backtest condition tree must have at least one comparison leaf.", "conditions");
        }

        _backtestBinder.ValidateTree(mergedTree, "conditions");
        BacktestConditionTreeDto treeDto = BacktestConditionTreeDtoMapper.ToDto(mergedTree);

        BacktestRiskManagementSettingsDto? riskDto = null;
        if (hasRisk)
        {
            if (riskElement.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "'riskManagement' must be an object.", "riskManagement");
            }

            if (riskElement.TryGetProperty("dynamicExits", out _))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    "'dynamicExits' cannot be stored in a BacktestConditionTemplate; only 'stopLossRatio' and 'takeProfitRatio' are persisted in templates.",
                    "riskManagement.dynamicExits");
            }

            BoundBacktestSettings boundSettings = BacktestSettingsBinder.Bind(arguments, timeFrame);
            BacktestRiskManagementSettings? boundRisk = boundSettings.RiskManagement;
            if (boundRisk is { IsEnabled: true })
            {
                riskDto = new BacktestRiskManagementSettingsDto
                {
                    StopLossPercent = boundRisk.StopLossPercent,
                    TakeProfitPercent = boundRisk.TakeProfitPercent,
                };
            }
        }
        else if (baseTemplate is not null)
        {
            riskDto = baseTemplate.RiskManagement;
        }

        string name = ResolveName(arguments, baseTemplate);
        string? description = ResolveDescription(arguments, baseTemplate);
        Guid templateId = ResolveTemplateId(arguments, baseTemplate);
        DateTime nowUtc = DateTime.UtcNow;

        var template = new BacktestConditionTemplate
        {
            Id = templateId,
            Name = name,
            Description = description,
            SchemaVersion = TemplateBase.DefaultSchemaVersion,
            CreatedAt = baseTemplate?.CreatedAt ?? nowUtc,
            UpdatedAt = nowUtc,
            IsFavorite = baseTemplate?.IsFavorite ?? false,
            ConditionTree = treeDto,
            RiskManagement = riskDto,
        };

        BacktestTemplateConversion conversion = _templateWriter.WriteBacktestTemplate(template);

        string templateJson = JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions());
        using var doc = JsonDocument.Parse(templateJson);
        JsonElement templateElement = doc.RootElement.Clone();

        int nodeCount = CanonicalRootsOf(mergedTree).Sum(CountGroupNodes);
        int depth = CanonicalRootsOf(mergedTree).Select(GroupDepth).DefaultIfEmpty(0).Max();

        string relativePath = $"Data/Templates/BacktestCondition/{templateId}.json";
        return new BuiltTemplateResult(
            TemplateType: "backtestCondition",
            Id: templateId,
            Name: name,
            Description: description,
            SchemaVersion: template.SchemaVersion,
            BaseTemplateId: baseId,
            MergeMode: mergeModeStr,
            SuggestedRelativePath: relativePath,
            NodeCount: nodeCount,
            Depth: depth,
            Warnings: Array.Empty<string>(),
            TemplateElement: templateElement,
            TemplateJson: templateJson,
            ConditionTree: null,
            Conditions: conversion.Conditions,
            RiskManagement: conversion.RiskManagement);
    }

    private static List<ScreenerIndicatorEntry> FlattenScreenerNode(IScreenerConditionNode node)
    {
        var entries = new List<ScreenerIndicatorEntry>();
        FlattenNodeRecursive(node, entries);
        if (entries.Count > 0)
        {
            entries[^1].LogicalOperator = LogicalOperator.And;
        }

        return entries;
    }

    private static void FlattenNodeRecursive(IScreenerConditionNode node, List<ScreenerIndicatorEntry> entries)
    {
        if (node is ScreenerConditionLeaf leaf)
        {
            entries.Add(leaf.Entry.Clone());
            return;
        }

        if (node is ScreenerConditionGroup group)
        {
            if (group.Children.Length == 0)
            {
                return;
            }

            for (int j = 0; j < group.Children.Length; j++)
            {
                IScreenerConditionNode child = group.Children[j];
                bool isFirst = j == 0;

                if (child is ScreenerConditionGroup childGroup)
                {
                    if (!isFirst && childGroup.Operator != group.Operator && childGroup.Children.Length > 1)
                    {
                        throw new ToolException(
                            ToolErrorCodes.InvalidArgument,
                            "A Screener template stores a sequential list of entries evaluated left-to-right; a nested group with a different operator on the right side cannot be represented without changing operator precedence. Place the nested group first, or use a uniform operator.",
                            "conditionTree");
                    }

                    var childEntries = new List<ScreenerIndicatorEntry>();
                    FlattenNodeRecursive(childGroup, childEntries);
                    if (childEntries.Count > 0 && j < group.Children.Length - 1)
                    {
                        childEntries[^1].LogicalOperator = group.Operator;
                    }

                    entries.AddRange(childEntries);
                }
                else if (child is ScreenerConditionLeaf childLeaf)
                {
                    ScreenerIndicatorEntry entry = childLeaf.Entry.Clone();
                    if (j < group.Children.Length - 1)
                    {
                        entry.LogicalOperator = group.Operator;
                    }

                    entries.Add(entry);
                }
            }
        }
    }

    private static BacktestConditionGroup MergeBacktestRoot(
        BacktestConditionGroup? baseGroup,
        BacktestConditionGroup? incomingGroup,
        TemplateMergeMode mergeMode)
    {
        bool baseHasLeaf = baseGroup is { HasLeaf: true };
        bool incomingHasLeaf = incomingGroup is { HasLeaf: true };

        if (!incomingHasLeaf)
        {
            return baseHasLeaf ? baseGroup! : BacktestConditionGroup.EmptyAnd;
        }

        if (!baseHasLeaf || mergeMode == TemplateMergeMode.Replace)
        {
            return incomingGroup!;
        }

        LogicalOperator op = mergeMode == TemplateMergeMode.AppendOr ? LogicalOperator.Or : LogicalOperator.And;
        var children = ImmutableArray.CreateBuilder<IBacktestConditionNode>();

        if (baseGroup!.Operator == op)
        {
            children.AddRange(baseGroup.Children);
        }
        else
        {
            children.Add(baseGroup);
        }

        if (incomingGroup!.Operator == op)
        {
            children.AddRange(incomingGroup.Children);
        }
        else
        {
            children.Add(incomingGroup);
        }

        return new BacktestConditionGroup(op, children.ToImmutable());
    }

    private static IEnumerable<BacktestConditionGroup> CanonicalRootsOf(BacktestConditionTree tree)
        => BacktestConditionTree.CanonicalRoots.Select(r => tree.Root(r.Section, r.Side));

    private static int CountGroupNodes(BacktestConditionGroup group)
    {
        if (!group.HasLeaf) return 0;
        int count = 1; // group itself
        foreach (IBacktestConditionNode child in group.Children)
        {
            count += child switch
            {
                BacktestConditionGroup nested => CountGroupNodes(nested),
                BacktestConditionLeaf => 1,
                _ => 0,
            };
        }
        return count;
    }

    private static int GroupDepth(BacktestConditionGroup group)
    {
        if (!group.HasLeaf) return 0;
        int maxChildDepth = 0;
        foreach (IBacktestConditionNode child in group.Children)
        {
            int d = child switch
            {
                BacktestConditionGroup nested => GroupDepth(nested),
                BacktestConditionLeaf => 1,
                _ => 0,
            };
            if (d > maxChildDepth) maxChildDepth = d;
        }
        return 1 + maxChildDepth;
    }

    private static string FormatEntryLabel(int index)
        => index < 26
            ? ((char)('A' + index)).ToString()
            : $"{(char)('A' + (index / 26) - 1)}{(char)('A' + (index % 26))}";

    private static string ResolveName(JsonElement arguments, TemplateBase? baseTemplate)
    {
        string? name = ToolArgs.String(arguments, "name");
        if (name is null)
        {
            if (baseTemplate is not null)
            {
                return baseTemplate.Name;
            }

            throw new ToolException(ToolErrorCodes.InvalidArgument, "'name' is required when creating a new template.", "name");
        }

        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'name' must be between 1 and 200 characters.", "name");
        }

        return name;
    }

    private static string? ResolveDescription(JsonElement arguments, TemplateBase? baseTemplate)
    {
        string? desc = ToolArgs.String(arguments, "description");
        if (desc is null)
        {
            return baseTemplate?.Description;
        }

        if (desc.Length > 1000)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'description' must be at most 1000 characters.", "description");
        }

        return desc;
    }

    private static Guid ResolveTemplateId(JsonElement arguments, TemplateBase? baseTemplate)
    {
        Guid? parsed = ParseOptionalGuid(arguments, "templateId");
        if (parsed is { } id)
        {
            return id;
        }

        return baseTemplate?.Id ?? Guid.NewGuid();
    }

    private static Guid? ParseOptionalGuid(JsonElement arguments, string name)
    {
        string? text = ToolArgs.String(arguments, name);
        if (text is null)
        {
            return null;
        }

        if (!Guid.TryParse(text, out Guid guid))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{name}' must be a valid GUID.", name);
        }

        return guid;
    }
}
