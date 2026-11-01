using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// A single entry or comparison in a saved template that cannot be converted to MCP JSON syntax, with its index and reason.
/// </summary>
internal sealed record TemplateUnconvertibleEntry(int Index, string Reason);

/// <summary>
/// The result of converting a saved Screener template to MCP syntax.
/// </summary>
internal sealed record ScreenerTemplateConversion(
    JsonNode ConditionTree,
    IReadOnlyList<TemplateUnconvertibleEntry> UnconvertibleEntries);

/// <summary>
/// The result of converting a saved Backtest condition template to MCP syntax.
/// </summary>
internal sealed record BacktestTemplateConversion(
    JsonNode Conditions,
    JsonObject? RiskManagement,
    IReadOnlyList<TemplateUnconvertibleEntry> UnconvertibleEntries);

/// <summary>
/// Converts saved domain templates (ScreenerIndicatorTemplate and BacktestConditionTemplate) into valid MCP query JSON syntax.
/// Supports round-trip consistency with ScreenerConditionBinder and BacktestConditionBinder.
/// </summary>
internal sealed class TemplateConditionWriter
{
    private readonly IndicatorCatalog? _indicators;
    private readonly ScreenerCatalog? _screenerCatalog;

    public TemplateConditionWriter(IndicatorCatalog? indicators = null, ScreenerCatalog? screenerCatalog = null)
    {
        _indicators = indicators;
        _screenerCatalog = screenerCatalog;
    }

    /// <summary>
    /// Writes a ScreenerIndicatorTemplate to MCP conditionTree JSON syntax.
    /// Enabled entries are chained sequentially (left-associative) using the preceding entry's LogicalOperator.
    /// Disabled or unconvertible entries are reported in <see cref="ScreenerTemplateConversion.UnconvertibleEntries"/>.
    /// </summary>
    public ScreenerTemplateConversion WriteScreenerTemplate(ScreenerIndicatorTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var unconvertible = new List<TemplateUnconvertibleEntry>();
        var convertedNodes = new List<JsonObject>();
        var operators = new List<LogicalOperator>();

        for (int i = 0; i < template.Entries.Count; i++)
        {
            ScreenerIndicatorEntry entry = template.Entries[i];
            if (!entry.IsEnabled)
            {
                unconvertible.Add(new TemplateUnconvertibleEntry(i, "Entry is disabled in template."));
                continue;
            }

            try
            {
                JsonObject comparison = WriteScreenerComparison(entry, i);
                convertedNodes.Add(comparison);
                if (convertedNodes.Count > 1)
                {
                    // The operator connecting the previous result with this entry is the previous enabled entry's LogicalOperator
                    // We store it for folding
                }

                operators.Add(entry.LogicalOperator);
            }
            catch (Exception ex)
            {
                unconvertible.Add(new TemplateUnconvertibleEntry(i, $"Cannot convert to MCP condition: {ex.Message}"));
            }
        }

        JsonNode treeNode;
        if (convertedNodes.Count == 0)
        {
            treeNode = new JsonObject
            {
                ["operator"] = LogicalOperator.And.ToString(),
                ["children"] = new JsonArray(),
            };
        }
        else if (convertedNodes.Count == 1)
        {
            treeNode = convertedNodes[0];
        }
        else
        {
            // Sequential left-associative folding: (E0 op0 E1) op1 E2 ...
            JsonObject current = new JsonObject
            {
                ["operator"] = operators[0].ToString(),
                ["children"] = new JsonArray { CloneNode(convertedNodes[0]), CloneNode(convertedNodes[1]) },
            };

            for (int i = 2; i < convertedNodes.Count; i++)
            {
                LogicalOperator nextOp = operators[i - 1];
                string opName = nextOp.ToString();

                if (string.Equals((string?)current["operator"], opName, StringComparison.Ordinal))
                {
                    ((JsonArray)current["children"]!).Add(CloneNode(convertedNodes[i]));
                }
                else
                {
                    current = new JsonObject
                    {
                        ["operator"] = opName,
                        ["children"] = new JsonArray { current, CloneNode(convertedNodes[i]) },
                    };
                }
            }

            treeNode = current;
        }

        return new ScreenerTemplateConversion(treeNode, unconvertible);
    }

    private JsonObject WriteScreenerComparison(ScreenerIndicatorEntry entry, int index)
    {
        JsonObject left = WriteScreenerSide(entry.LeftHand);
        string op = entry.Operator.ToString();

        var node = new JsonObject
        {
            ["left"] = left,
            ["comparison"] = op,
        };

        switch (entry.TargetMode)
        {
            case RightHandTargetMode.NumericValue:
                node["right"] = entry.RightNumericValue;
                break;

            case RightHandTargetMode.StringValue:
                node["right"] = entry.RightStringValue;
                break;

            case RightHandTargetMode.Indicator:
                node["right"] = WriteScreenerSide(entry.RightHand);
                break;

            default:
                throw new InvalidOperationException($"Unsupported TargetMode {entry.TargetMode}.");
        }

        return node;
    }

    private JsonObject WriteScreenerSide(ScreenerIndicatorSideConfig side)
    {
        var obj = new JsonObject();

        switch (side.CategoryType)
        {
            case ScreenerItemCategoryType.Indicator:
                if (side.IndicatorType == IndicatorType.Price)
                {
                    string priceSource = !string.IsNullOrWhiteSpace(side.OutputName) ? side.OutputName : "Close";
                    obj["price"] = priceSource;
                }
                else
                {
                    obj["indicator"] = side.IndicatorType.ToString();
                    if (side.Parameters != null && side.Parameters.Count > 0)
                    {
                        IndicatorCatalogEntry? entry = _indicators?.Find(side.IndicatorType.ToString());
                        var paramNames = entry?.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var paramsObj = new JsonObject();
                        foreach (var (k, v) in side.Parameters)
                        {
                            if (paramNames is not null && !paramNames.Contains(k) && (!entry!.IsCrossTicker || !string.Equals(k, "ComparisonSymbol", StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            WriteParameterValue(paramsObj, k, v);
                        }

                        if (paramsObj.Count > 0)
                        {
                            obj["parameters"] = paramsObj;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(side.OutputName)
                        && !string.Equals(side.OutputName, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(side.OutputName, side.IndicatorType.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        obj["outputSeriesName"] = side.OutputName;
                    }
                }

                break;

            case ScreenerItemCategoryType.Column:
                string colName = side.ResolveLookupName() ?? side.OutputName;
                if (string.IsNullOrWhiteSpace(colName))
                {
                    colName = side.CustomDisplayName;
                }

                if (_screenerCatalog is not null)
                {
                    var colItem = _screenerCatalog.FindColumn(colName);
                    if (colItem is not null && !_screenerCatalog.IsEvaluable(colItem))
                    {
                        throw new InvalidOperationException($"Column '{colName}' cannot be evaluated by the screener.");
                    }
                }

                obj["column"] = colName;
                break;

            case ScreenerItemCategoryType.Criteria:
                string critName = side.ResolveCriteriaName() ?? side.OutputName;
                if (string.IsNullOrWhiteSpace(critName))
                {
                    critName = side.CustomDisplayName;
                }

                obj["criteria"] = critName;
                break;

            default:
                throw new InvalidOperationException($"Unsupported CategoryType {side.CategoryType}.");
        }

        if (side.Offset > 0)
        {
            obj["offset"] = side.Offset;
        }

        if (side.TimeFrame != TimeFrame.D1 && ScreenerSideFrames.ReadsBars(side))
        {
            obj["timeFrame"] = side.TimeFrame.ToString();
        }

        return obj;
    }

    /// <summary>
    /// Writes a BacktestConditionTemplate to MCP conditions and riskManagement JSON syntax.
    /// Canonical roots (entryLong, entryShort, exitLong, exitShort, reverseLong, reverseShort) are mapped.
    /// </summary>
    public BacktestTemplateConversion WriteBacktestTemplate(BacktestConditionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var unconvertible = new List<TemplateUnconvertibleEntry>();
        var conditionsObj = new JsonObject();
        int leafCounter = 0;

        WriteBacktestRoot(conditionsObj, "entryLong", template.ConditionTree.EntryLong, ref leafCounter, unconvertible);
        WriteBacktestRoot(conditionsObj, "entryShort", template.ConditionTree.EntryShort, ref leafCounter, unconvertible);
        WriteBacktestRoot(conditionsObj, "exitLong", template.ConditionTree.ExitLong, ref leafCounter, unconvertible);
        WriteBacktestRoot(conditionsObj, "exitShort", template.ConditionTree.ExitShort, ref leafCounter, unconvertible);
        WriteBacktestRoot(conditionsObj, "reverseLong", template.ConditionTree.ReverseLong, ref leafCounter, unconvertible);
        WriteBacktestRoot(conditionsObj, "reverseShort", template.ConditionTree.ReverseShort, ref leafCounter, unconvertible);

        JsonObject? riskObj = null;
        if (template.RiskManagement is { } risk && (risk.StopLossPercent.HasValue || risk.TakeProfitPercent.HasValue))
        {
            riskObj = new JsonObject();
            if (risk.StopLossPercent is { } stop)
            {
                riskObj["stopLossRatio"] = stop;
            }

            if (risk.TakeProfitPercent is { } take)
            {
                riskObj["takeProfitRatio"] = take;
            }
        }

        return new BacktestTemplateConversion(conditionsObj, riskObj, unconvertible);
    }

    private void WriteBacktestRoot(JsonObject conditionsObj, string rootKey, BacktestConditionNodeDto? rootNode, ref int leafCounter, List<TemplateUnconvertibleEntry> unconvertible)
    {
        if (rootNode is null)
        {
            return;
        }

        if (rootNode.Kind != BacktestConditionNodeKind.Group)
        {
            unconvertible.Add(new TemplateUnconvertibleEntry(leafCounter++, $"Root '{rootKey}' must be a Group node."));
            return;
        }

        if (rootNode.Children is null || rootNode.Children.Count == 0)
        {
            return; // Empty root is omitted from conditions
        }

        JsonNode? convertedGroup = WriteBacktestNode(rootNode, ref leafCounter, unconvertible);
        if (convertedGroup is not null)
        {
            conditionsObj[rootKey] = convertedGroup;
        }
    }

    private JsonNode? WriteBacktestNode(BacktestConditionNodeDto node, ref int leafCounter, List<TemplateUnconvertibleEntry> unconvertible)
    {
        if (node.Kind == BacktestConditionNodeKind.Group)
        {
            if (node.Children is null || node.Children.Count == 0)
            {
                return null;
            }

            var childrenArray = new JsonArray();
            foreach (BacktestConditionNodeDto child in node.Children)
            {
                JsonNode? convertedChild = WriteBacktestNode(child, ref leafCounter, unconvertible);
                if (convertedChild is not null)
                {
                    childrenArray.Add(convertedChild);
                }
            }

            if (childrenArray.Count == 0)
            {
                return null;
            }

            return new JsonObject
            {
                ["operator"] = node.Operator.ToString(),
                ["children"] = childrenArray,
            };
        }

        if (node.Kind == BacktestConditionNodeKind.Leaf)
        {
            int index = leafCounter++;
            if (node.Comparison is null)
            {
                unconvertible.Add(new TemplateUnconvertibleEntry(index, "Leaf node has no comparison."));
                return null;
            }

            BacktestConditionEntryDto comp = node.Comparison;
            if (BacktestConditionBinder.ExcludedComparisons.ContainsKey(comp.Operator))
            {
                unconvertible.Add(new TemplateUnconvertibleEntry(index, $"Operator '{comp.Operator}' cannot be run in backtest: {BacktestConditionBinder.ExcludedComparisons[comp.Operator]}"));
                return null;
            }

            try
            {
                JsonObject left = WriteBacktestSide(comp.Left);
                var leafObj = new JsonObject
                {
                    ["left"] = left,
                    ["comparison"] = comp.Operator.ToString(),
                };

                switch (comp.TargetMode)
                {
                    case RightHandTargetMode.NumericValue:
                        leafObj["right"] = comp.RightNumericValue;
                        break;

                    case RightHandTargetMode.Indicator:
                        if (comp.Right is null)
                        {
                            throw new InvalidOperationException("Right side indicator is null.");
                        }

                        leafObj["right"] = WriteBacktestSide(comp.Right);
                        break;

                    default:
                        throw new InvalidOperationException($"Unsupported TargetMode {comp.TargetMode}.");
                }

                return leafObj;
            }
            catch (Exception ex)
            {
                unconvertible.Add(new TemplateUnconvertibleEntry(index, $"Cannot convert comparison: {ex.Message}"));
                return null;
            }
        }

        unconvertible.Add(new TemplateUnconvertibleEntry(leafCounter++, $"Unknown node kind {node.Kind}."));
        return null;
    }

    private JsonObject WriteBacktestSide(BacktestConditionSideDto? side)
    {
        if (side is null)
        {
            throw new ArgumentNullException(nameof(side));
        }

        var obj = new JsonObject();
        if (side.IndicatorType == IndicatorType.Price)
        {
            obj["price"] = side.PriceSource.ToString();
        }
        else
        {
            obj["indicator"] = side.IndicatorType.ToString();
            if (side.Parameters is not null)
            {
                var paramsObj = new JsonObject();
                IndicatorCatalogEntry? entry = _indicators?.Find(side.IndicatorType.ToString());
                var paramNames = entry?.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (PropertyInfo property in side.Parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    if (paramNames is not null)
                    {
                        if (!paramNames.Contains(property.Name) && (!entry!.IsCrossTicker || !string.Equals(property.Name, "ComparisonSymbol", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (property.DeclaringType == typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableValidator)
                            || property.DeclaringType == typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)
                            || property.DeclaringType == typeof(CoreIndicatorParameterBase))
                        {
                            continue;
                        }
                    }

                    object? value = property.GetValue(side.Parameters);
                    if (value is not null)
                    {
                        WriteParameterValue(paramsObj, property.Name, value);
                    }
                }

                if (paramsObj.Count > 0)
                {
                    obj["parameters"] = paramsObj;
                }
            }

            if (!string.IsNullOrWhiteSpace(side.OutputName)
                && !string.Equals(side.OutputName, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(side.OutputName, side.IndicatorType.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                obj["outputSeriesName"] = side.OutputName;
            }
        }

        if (side.Offset > 0)
        {
            obj["offset"] = side.Offset;
        }

        if (side.Frame.HasValue && side.Frame.Value != TimeFrame.D1)
        {
            obj["frame"] = side.Frame.Value.ToString();
        }

        return obj;
    }

    private static void WriteParameterValue(JsonObject obj, string key, object? value)
    {
        switch (value)
        {
            case null:
                obj[key] = null;
                break;
            case int i:
                obj[key] = i;
                break;
            case long l:
                obj[key] = l;
                break;
            case decimal m:
                obj[key] = m;
                break;
            case double d:
                obj[key] = (decimal)d;
                break;
            case float f:
                obj[key] = (decimal)f;
                break;
            case bool b:
                obj[key] = b;
                break;
            case string s:
                obj[key] = s;
                break;
            case Enum e:
                obj[key] = e.ToString();
                break;
            case JsonElement je:
                obj[key] = JsonNode.Parse(je.GetRawText());
                break;
            default:
                obj[key] = value.ToString();
                break;
        }
    }

    private static JsonNode CloneNode(JsonNode node)
        => JsonNode.Parse(node.ToJsonString())!;
}
