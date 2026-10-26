using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>Typed reads of arguments that the schema check has already shown to have the right JSON type.</summary>
internal static class ToolArgs
{
    public static string? String(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    public static long? Integer(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) ? value.GetInt64() : null;

    public static double? Number(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.TryGetDouble(out double number))
        {
            if (!double.IsFinite(number))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' must be a finite number.", name);
            }

            return number;
        }

        return null;
    }

    public static bool? Boolean(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : null;

    public static IReadOnlyList<string>? StringArray(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value)
            ? value.EnumerateArray().Select(item => item.GetString()!).ToList()
            : null;

    /// <summary>The names of the time frames the data layer can load, for descriptions and hints.</summary>
    public static string SupportedTimeFrameNames => string.Join(", ", ReadOnlyGatewayContract.SupportedTimeFrames);

    /// <summary>A required time-frame argument (the schema enum guarantees a defined name); frames the data layer cannot load are UNSUPPORTED_TIMEFRAME.</summary>
    public static TimeFrame SupportedTimeFrame(JsonElement arguments, string name)
    {
        TimeFrame timeFrame = Enum.Parse<TimeFrame>(String(arguments, name)!);
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                $"Time frame {timeFrame} is not supported.",
                name,
                $"Supported: {SupportedTimeFrameNames}.");
        }

        return timeFrame;
    }

    /// <summary>Parses a yyyy-MM-dd date argument; anything else is INVALID_ARGUMENT naming the argument.</summary>
    public static DateOnly? Date(JsonElement arguments, string name)
    {
        string? text = String(arguments, name);
        if (text is null)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(text, ToolJson.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' must be a date in the form {ToolJson.DateFormat}.", name);
        }

        return date;
    }

    /// <summary>An integer argument that must be at least 1 and no larger than <paramref name="maximum"/> (LIMIT_EXCEEDED above it).</summary>
    public static int PositiveWithin(JsonElement arguments, string name, int defaultValue, int maximum)
    {
        long? value = Integer(arguments, name);
        if (value is null)
        {
            return defaultValue;
        }

        if (value < 1)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' must be at least 1.", name);
        }

        if (value > maximum)
        {
            throw new ToolException(ToolErrorCodes.LimitExceeded, $"Argument '{name}' is larger than the limit {maximum}.", name, $"Use {maximum} or less.");
        }

        return (int)value;
    }

    /// <summary>An integer argument that must be 0 or more and no larger than <paramref name="maximum"/> (LIMIT_EXCEEDED above it).</summary>
    public static int NonNegativeWithin(JsonElement arguments, string name, int defaultValue, int maximum)
    {
        long? value = Integer(arguments, name);
        if (value is null)
        {
            return defaultValue;
        }

        if (value < 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' must be 0 or more.", name);
        }

        if (value > maximum)
        {
            throw new ToolException(ToolErrorCodes.LimitExceeded, $"Argument '{name}' is larger than the limit {maximum}.", name, $"Use {maximum} or less.");
        }

        return (int)value;
    }
}
