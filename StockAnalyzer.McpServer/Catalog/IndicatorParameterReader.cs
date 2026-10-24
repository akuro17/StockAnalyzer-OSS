using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// One tunable parameter of an indicator as the AI client sees it. <see cref="Min"/> and <see cref="Max"/> come from
/// [CoreParameterRange] (the bound plan S-10 names); <see cref="GuiMin"/> and <see cref="GuiMax"/> come from [Range], which the
/// chart's input field enforces and which is narrower for some parameters. Both are reported, neither is chosen for the client.
/// </summary>
internal sealed record IndicatorParameterDescriptor(
    string Name,
    string DisplayName,
    string? Description,
    string Type,
    object? Default,
    decimal? Min,
    decimal? Max,
    decimal? GuiMin,
    decimal? GuiMax,
    IReadOnlyList<string>? AllowedValues);

/// <summary>
/// Lists the parameters of an indicator's parameter object with the same selection rule as the chart's parameter form
/// (<c>ParameterViewBuilder</c>): public read/write properties that carry a display name, [Range] or [CoreParameterRange],
/// except [Browsable(false)], category "Display" and ShowSubWindowBar.
/// </summary>
internal static class IndicatorParameterReader
{
    public const string IntegerType = "integer";
    public const string NumberType = "number";
    public const string BooleanType = "boolean";
    public const string StringType = "string";
    public const string EnumType = "enum";
    public const string ColorType = "color";

    private const string DisplayCategory = "Display";
    private const string ShowSubWindowBarName = "ShowSubWindowBar";

    public static IReadOnlyList<IndicatorParameterDescriptor> Read(CoreIndicatorParameterBase parameters)
    {
        var descriptors = new List<IndicatorParameterDescriptor>();
        foreach (PropertyInfo property in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            BrowsableAttribute? browsable = property.GetCustomAttribute<BrowsableAttribute>();
            CategoryAttribute? category = property.GetCustomAttribute<CategoryAttribute>();
            DisplayNameAttribute? displayName = property.GetCustomAttribute<DisplayNameAttribute>();
            DescriptionAttribute? description = property.GetCustomAttribute<DescriptionAttribute>();
            RangeAttribute? range = property.GetCustomAttribute<RangeAttribute>();
            CoreParameterRangeAttribute? coreRange = property.GetCustomAttribute<CoreParameterRangeAttribute>();

            if (browsable is { Browsable: false }
                || (category is not null && string.Equals(category.Category, DisplayCategory, StringComparison.OrdinalIgnoreCase))
                || string.Equals(property.Name, ShowSubWindowBarName, StringComparison.OrdinalIgnoreCase)
                || (displayName is null && range is null && coreRange is null))
            {
                continue;
            }

            Type clrType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            descriptors.Add(new IndicatorParameterDescriptor(
                property.Name,
                displayName?.DisplayName ?? property.Name,
                description?.Description,
                TypeNameOf(clrType),
                DefaultOf(clrType, property.GetValue(parameters)),
                ToDecimal(coreRange?.Minimum),
                ToDecimal(coreRange?.Maximum),
                ToDecimal(range?.Minimum),
                ToDecimal(range?.Maximum),
                clrType.IsEnum ? Enum.GetNames(clrType) : null));
        }

        return descriptors;
    }

    private static string TypeNameOf(Type type)
    {
        if (type.IsEnum)
        {
            return EnumType;
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
        {
            return IntegerType;
        }

        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
        {
            return NumberType;
        }

        if (type == typeof(bool))
        {
            return BooleanType;
        }

        if (type == typeof(string))
        {
            return StringType;
        }

        return type == typeof(IndicatorColor) ? ColorType : type.Name;
    }

    /// <summary>The default as a JSON-writable value: numbers and booleans as they are, text for enums and colors, null when unset or not finite.</summary>
    private static object? DefaultOf(Type clrType, object? value) => value switch
    {
        null => null,
        double number when !double.IsFinite(number) => null,
        float number when !float.IsFinite(number) => null,
        float number => (decimal)number,
        _ when clrType.IsEnum => value.ToString(),
        IndicatorColor color => color.ToString(),
        _ => value,
    };

    private static decimal? ToDecimal(object? bound)
    {
        if (bound is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDecimal(bound, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
