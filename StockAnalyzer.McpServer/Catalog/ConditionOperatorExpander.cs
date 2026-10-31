using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.McpServer.Tools;
using static StockAnalyzer.McpServer.Catalog.ConditionJson;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// The comparison names the MCP binders accept on top of the application's <see cref="ComparisonOperator"/> values, and their exact expansion into
/// comparisons the engines already evaluate (plan 05 / T2; Core is unchanged). With <c>a</c> and <c>b</c> sides and <c>a[k]</c> the same side with its
/// <c>offset</c> raised by k (a constant right side stays the constant):
/// <list type="bullet">
/// <item><c>CrossesAbove(a, b)</c> = <c>a[0] &gt; b[0]</c> And <c>a[1] &lt;= b[1]</c> (Pine <c>ta.crossover</c>; owner decision D-05f).</item>
/// <item><c>CrossesBelow(a, b)</c> = <c>a[0] &lt; b[0]</c> And <c>a[1] &gt;= b[1]</c>.</item>
/// <item><c>Rising(a, bars)</c> = <c>a[0] &gt; a[1]</c> And <c>a[1] &gt; a[2]</c> And ... And <c>a[bars-1] &gt; a[bars]</c>: <c>bars</c> comparisons, every bar consecutive (D-05e).</item>
/// <item><c>Falling(a, bars)</c> = the same with <c>&lt;</c>.</item>
/// </list>
/// A pure function of the leaf's JSON: it neither reads settings nor binds sides, so the binders keep every other check (keys, names, parameters, limits) in one place.
/// The names are deliberately not in the binders' <c>ComparisonNames</c>, which must stay a subset of the real <see cref="ComparisonOperator"/> values (McpParityGuardTests).
/// </summary>
internal static class ConditionOperatorExpander
{
    public const string CrossesAbove = nameof(CrossesAbove);
    public const string CrossesBelow = nameof(CrossesBelow);
    public const string Rising = nameof(Rising);
    public const string Falling = nameof(Falling);

    /// <summary>The leaf key that gives the number of consecutive bars of <see cref="Rising"/> and <see cref="Falling"/>.</summary>
    public const string BarsKey = "bars";

    private const string OffsetKey = "offset";
    private const string LeftKey = "left";
    private const string RightKey = "right";
    private const string ComparisonKey = "comparison";

    /// <summary>The keys of a leaf that uses one of the expanded names.</summary>
    public static readonly string[] LeafKeys = { LeftKey, ComparisonKey, RightKey, BarsKey };

    /// <summary>Every name this expander accepts, in the order the tool descriptions list them.</summary>
    public static IReadOnlyList<string> ExpandedComparisonNames { get; } = new[] { CrossesAbove, CrossesBelow, Rising, Falling };

    /// <summary>The one sentence group the tool descriptions of the Screener and the Backtest use to explain the expanded names (a single definition).</summary>
    public static string Description { get; } =
        $"Besides those, {string.Join(", ", ExpandedComparisonNames)} are accepted as comparison and are expanded exactly into the comparisons above, joined by And (the result shows the expansion): " +
        "CrossesAbove = left > right now and left <= right one bar earlier; CrossesBelow = left < right now and left >= right one bar earlier (a number as right stays that number); " +
        "Rising and Falling take bars N (1 up to the largest offset) instead of right: every one of the last N bar-to-bar steps of left goes up (Rising) or down (Falling), " +
        "that is left[0] > left[1] and left[1] > left[2] ... and left[N-1] > left[N] for Rising. " +
        "The expanded comparisons count against the tree-size limits; an offset that the expansion would push past the largest offset is INVALID_ARGUMENT.";

    /// <summary>True when the leaf's <c>comparison</c> is one of <see cref="ExpandedComparisonNames"/> (matched like the binders match an operator: trimmed, any case).</summary>
    public static bool TryGetName(JsonElement leaf, out string name)
    {
        name = string.Empty;
        if (leaf.ValueKind != JsonValueKind.Object
            || !leaf.TryGetProperty(ComparisonKey, out JsonElement comparison)
            || comparison.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = comparison.GetString()!.Trim();
        string? match = ExpandedComparisonNames.FirstOrDefault(candidate => string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        name = match;
        return true;
    }

    /// <summary>
    /// The comparisons the leaf stands for, as leaf objects of the ordinary grammar (<c>left</c>, <c>comparison</c>, <c>right</c>), in evaluation order; all of them are to be
    /// combined with And. One comparison (<c>Rising</c> with <c>bars</c> 1) is returned alone.
    /// </summary>
    /// <param name="path">The path of the leaf, for error parameters.</param>
    /// <param name="maxOffset">The largest offset the consumer accepts (its setting, never a constant here).</param>
    /// <param name="offsetSetting">The name of that setting, for the hint.</param>
    /// <exception cref="ToolException">INVALID_ARGUMENT: a key that does not belong to the name, a missing or out-of-range <c>bars</c>, or an offset that the expansion would push past <paramref name="maxOffset"/>.</exception>
    public static IReadOnlyList<JsonElement> Expand(JsonElement leaf, string name, string path, int maxOffset, string offsetSetting)
    {
        JsonNode left = Parse(Require(leaf, LeftKey, path, JsonValueKind.Object));
        bool isCross = name is CrossesAbove or CrossesBelow;

        var nodes = new List<JsonObject>();
        if (isCross)
        {
            if (leaf.TryGetProperty(BarsKey, out _))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{BarsKey} belongs to {Rising} and {Falling} only.", $"{path}.{BarsKey}", $"{name} compares two sides one bar apart; remove {BarsKey}.");
            }

            JsonNode right = Parse(Require(leaf, RightKey, path));
            RequireWithinOffset(left, 1, maxOffset, offsetSetting, path);
            RequireWithinOffset(right, 1, maxOffset, offsetSetting, path);
            bool above = name == CrossesAbove;
            nodes.Add(Comparison(Shift(left, 0), above ? ComparisonOperator.GreaterThan : ComparisonOperator.LessThan, Shift(right, 0)));
            nodes.Add(Comparison(Shift(left, 1), above ? ComparisonOperator.LessThanOrEqual : ComparisonOperator.GreaterThanOrEqual, Shift(right, 1)));
        }
        else
        {
            if (leaf.TryGetProperty(RightKey, out _))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{name} compares a side with its own earlier values and has no right side.", $"{path}.{RightKey}", $"Remove right and give {BarsKey}.");
            }

            JsonElement barsElement = Require(leaf, BarsKey, path, JsonValueKind.Number);
            if (!barsElement.TryGetInt32(out int bars) || bars < 1 || bars > maxOffset)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"{BarsKey} is a whole number of bars from 1 to the largest offset.",
                    $"{path}.{BarsKey}",
                    $"The largest offset is the application setting {offsetSetting} = {maxOffset}.");
            }

            RequireWithinOffset(left, bars, maxOffset, offsetSetting, path);
            ComparisonOperator step = name == Rising ? ComparisonOperator.GreaterThan : ComparisonOperator.LessThan;
            for (int index = 0; index < bars; index++)
            {
                nodes.Add(Comparison(Shift(left, index), step, Shift(left, index + 1)));
            }
        }

        return nodes.Select(ToElement).ToArray();
    }

    /// <summary>The node as a standalone element: the clone outlives the parsed document, which is disposed here.</summary>
    private static JsonElement ToElement(JsonNode node)
    {
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject Comparison(JsonNode left, ComparisonOperator op, JsonNode right)
        => new()
        {
            [LeftKey] = left,
            [ComparisonKey] = op.ToString(),
            [RightKey] = right,
        };

    private static JsonNode Parse(JsonElement element) => JsonNode.Parse(element.GetRawText())!;

    /// <summary>The side with its offset raised by <paramref name="bars"/>; a number (a constant right side) is returned as it is.</summary>
    private static JsonNode Shift(JsonNode side, int bars)
    {
        JsonNode copy = side.DeepClone();
        if (bars == 0 || copy is not JsonObject obj)
        {
            return copy;
        }

        // A side whose offset is not a valid whole number is left as it is: binding the first comparison reports that offset before any other is used.
        if (ReadOffset(obj) is { } offset)
        {
            obj[OffsetKey] = offset + bars;
        }

        return copy;
    }

    /// <summary>
    /// The expansion must not push a side's offset past the consumer's limit. A side that already exceeds it, or whose offset is not a valid number, is not judged here:
    /// the ordinary side binding refuses it with its own, more precise error.
    /// </summary>
    private static void RequireWithinOffset(JsonNode side, int shift, int maxOffset, string offsetSetting, string path)
    {
        if (side is JsonObject obj && ReadOffset(obj) is { } offset && offset <= maxOffset && offset + shift > maxOffset)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"The expansion reads {shift} bar(s) before offset {offset}, which is above the largest offset {maxOffset}.",
                path,
                $"The largest offset is the application setting {offsetSetting} = {maxOffset}; use a smaller offset or fewer bars.");
        }
    }

    private static int? ReadOffset(JsonObject side)
    {
        if (!side.TryGetPropertyValue(OffsetKey, out JsonNode? node))
        {
            return 0;
        }

        return node is JsonValue value && value.TryGetValue(out int offset) && offset >= 0 ? offset : null;
    }
}
