using System.Globalization;
using System.Text.Json.Nodes;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One entry of parameterRanges: the values are Start, Start + Step, ... up to the largest one that does not exceed End.</summary>
internal sealed record ParameterRange(string Name, decimal Start, decimal End, decimal Step);

/// <summary>
/// The grid of a parameter sweep and the placeholder substitution it needs (pure functions, no I/O). Combinations are numbered 0.. in the order of the
/// ranges, the FIRST range changing slowest. Values are exact decimals (Start + k * Step, never accumulated), so 0.1, 0.2, 0.3 stay 0.1, 0.2, 0.3.
/// A placeholder is the JSON object {"$param": "name"} standing where a number goes.
/// </summary>
internal sealed class BacktestParameterGrid
{
    public const string PlaceholderKey = "$param";
    public const string RangesArgument = "parameterRanges";

    private readonly decimal[][] _values;
    private readonly int[] _strides;

    private BacktestParameterGrid(IReadOnlyList<ParameterRange> ranges, decimal[][] values, int combinationCount)
    {
        Ranges = ranges;
        _values = values;
        CombinationCount = combinationCount;
        _strides = new int[values.Length];
        int stride = 1;
        for (int i = values.Length - 1; i >= 0; i--)
        {
            _strides[i] = stride;
            stride *= values[i].Length;
        }
    }

    public IReadOnlyList<ParameterRange> Ranges { get; }

    public int CombinationCount { get; }

    /// <summary>How many values the parameter at <paramref name="parameter"/> takes.</summary>
    public int ValueCount(int parameter) => _values[parameter].Length;

    /// <summary>
    /// Checks the ranges and builds the grid.
    /// </summary>
    /// <exception cref="ToolException">INVALID_ARGUMENT for an empty or repeated name, a step of 0 or less, or Start above End; LIMIT_EXCEEDED when the number of
    /// combinations (the product of the value counts, computed without overflow) is above <paramref name="maxCombinations"/>.</exception>
    public static BacktestParameterGrid Create(IReadOnlyList<ParameterRange> ranges, int maxCombinations)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "parameterRanges needs at least one range.", RangesArgument);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var counts = new long[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            ParameterRange range = ranges[i];
            string path = $"{RangesArgument}[{i}]";
            if (string.IsNullOrWhiteSpace(range.Name))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "A parameter range needs a name.", $"{path}.name");
            }

            if (!names.Add(range.Name))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"The parameter name '{range.Name}' is used twice.", $"{path}.name");
            }

            if (range.Step <= 0m)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "step must be greater than 0.", $"{path}.step");
            }

            if (range.Start > range.End)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "start must not be greater than end.", $"{path}.start");
            }

            counts[i] = ValueCountOf(range, maxCombinations);
        }

        long product = 1;
        for (int i = 0; i < counts.Length; i++)
        {
            try
            {
                product = checked(product * counts[i]);
            }
            catch (OverflowException)
            {
                throw TooMany($"more than {long.MaxValue.ToString(CultureInfo.InvariantCulture)}", maxCombinations);
            }

            if (product > maxCombinations)
            {
                throw TooMany(i == counts.Length - 1 ? product.ToString(CultureInfo.InvariantCulture) : $"more than {maxCombinations}", maxCombinations);
            }
        }

        var values = new decimal[ranges.Count][];
        for (int i = 0; i < ranges.Count; i++)
        {
            values[i] = new decimal[counts[i]];
            for (int k = 0; k < values[i].Length; k++)
            {
                values[i][k] = Normalize(ranges[i].Start + (k * ranges[i].Step));
            }
        }

        return new BacktestParameterGrid(ranges, values, (int)product);
    }

    private static long ValueCountOf(ParameterRange range, int maxCombinations)
    {
        decimal count;
        try
        {
            count = decimal.Floor((range.End - range.Start) / range.Step) + 1m;
        }
        catch (OverflowException)
        {
            throw TooMany("more than " + maxCombinations.ToString(CultureInfo.InvariantCulture), maxCombinations);
        }

        if (count > maxCombinations)
        {
            throw TooMany($"more than {maxCombinations} (this range alone has {count.ToString("0", CultureInfo.InvariantCulture)} values)", maxCombinations);
        }

        return (long)count;
    }

    private static ToolException TooMany(string count, int maxCombinations)
        => new(
            ToolErrorCodes.LimitExceeded,
            $"The ranges give {count} combinations; the limit is {maxCombinations}.",
            RangesArgument,
            "Use a coarser step, a narrower range or fewer parameters. The grid is never thinned.");

    /// <summary>The value of parameter <paramref name="parameter"/> at <paramref name="position"/> (0-based).</summary>
    public decimal ValueAt(int parameter, int position) => _values[parameter][position];

    /// <summary>The position of every parameter in combination <paramref name="index"/>.</summary>
    public int[] Positions(int index)
    {
        if (index < 0 || index >= CombinationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var positions = new int[_values.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = index / _strides[i] % _values[i].Length;
        }

        return positions;
    }

    /// <summary>The name and value of every parameter in combination <paramref name="index"/>, in the order of the ranges.</summary>
    public IReadOnlyList<KeyValuePair<string, decimal>> ValuesAt(int index)
    {
        int[] positions = Positions(index);
        var values = new KeyValuePair<string, decimal>[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            values[i] = new KeyValuePair<string, decimal>(Ranges[i].Name, _values[i][positions[i]]);
        }

        return values;
    }

    /// <summary>
    /// The combinations that differ from <paramref name="index"/> in exactly one parameter by exactly one step (down, then up, for each parameter in the
    /// order of the ranges); a step that would leave the range is not a neighbour.
    /// </summary>
    public IReadOnlyList<int> Neighbors(int index)
    {
        int[] positions = Positions(index);
        var neighbors = new List<int>(2 * positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            if (positions[i] > 0)
            {
                neighbors.Add(index - _strides[i]);
            }

            if (positions[i] < _values[i].Length - 1)
            {
                neighbors.Add(index + _strides[i]);
            }
        }

        return neighbors;
    }

    /// <summary>The same number with no trailing zeros (14.0 is 14), so a whole value is written and read back as a whole number.</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    // ---- placeholders ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The names of the placeholders inside <paramref name="node"/> in document order (repeats included). A {"$param": ...} object must hold only that key,
    /// with a non-empty text as value.
    /// </summary>
    /// <exception cref="ToolException">INVALID_ARGUMENT naming the place of a malformed placeholder.</exception>
    public static IReadOnlyList<(string Name, string Path)> FindPlaceholders(JsonNode? node, string path)
    {
        var names = new List<(string Name, string Path)>();
        Collect(node, path, names);
        return names;
    }

    private static void Collect(JsonNode? node, string path, List<(string Name, string Path)> names)
    {
        switch (node)
        {
            case JsonObject obj when obj.ContainsKey(PlaceholderKey):
                names.Add((PlaceholderName(obj, path), path));
                break;
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    Collect(property.Value, $"{path}.{property.Key}", names);
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    Collect(array[i], $"{path}[{i}]", names);
                }

                break;
        }
    }

    private static string PlaceholderName(JsonObject obj, string path)
    {
        if (obj.Count != 1 || obj[PlaceholderKey] is not JsonValue value || !value.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"A placeholder is an object with the one key '{PlaceholderKey}' and the name of a parameter range as text.",
                path,
                "Example: {\"$param\": \"period\"}.");
        }

        return name;
    }

    /// <summary>A copy of <paramref name="node"/> with every placeholder replaced by the number its name has in <paramref name="values"/>.</summary>
    /// <exception cref="KeyNotFoundException">A placeholder names a parameter that has no value (callers check the names first).</exception>
    public static JsonNode? Substitute(JsonNode? node, IReadOnlyDictionary<string, decimal> values)
    {
        switch (node)
        {
            case JsonObject obj when obj.ContainsKey(PlaceholderKey):
                return JsonValue.Create(values[PlaceholderName(obj, PlaceholderKey)]);
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    copy.Add(property.Key, Substitute(property.Value, values));
                }

                return copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (JsonNode? item in array)
                {
                    items.Add(Substitute(item, values));
                }

                return items;
            default:
                return node?.DeepClone();
        }
    }
}
