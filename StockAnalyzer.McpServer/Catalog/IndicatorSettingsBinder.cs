using System.Globalization;
using System.Reflection;
using System.Text.Json;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Builds the settings of one requested indicator: the default settings the Screener uses for the type, with the client's
/// parameter values applied strictly (names case-insensitive, unknown names rejected, the JSON type of each value checked
/// against the descriptor, enum names parsed, [CoreParameterRange] bounds enforced, then the parameter object's own Validate, both through <see cref="IndicatorParameterRules"/>).
/// </summary>
internal sealed class IndicatorSettingsBinder
{
    private readonly IScreenerCatalogProvider _catalogProvider;
    private readonly IIndicatorFactory _factory;

    public IndicatorSettingsBinder(IScreenerCatalogProvider catalogProvider, IIndicatorFactory factory)
    {
        _catalogProvider = catalogProvider;
        _factory = factory;
    }

    /// <param name="path">The argument path of the indicator, for example <c>indicators[0]</c>; parameter errors name <c>path.parameters.Name</c>.</param>
    public CoreIndicatorSettings Bind(IndicatorCatalogEntry entry, JsonElement parameters, PriceType? priceSource, string path)
    {
        CoreIndicatorSettings settings = _catalogProvider.GetDefaultSettings(Enum.Parse<IndicatorType>(entry.Type), _factory)
            ?? throw new InvalidOperationException($"No default settings exist for {entry.Type}.");
        settings.IsEnabled = true;

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty supplied in parameters.EnumerateObject())
            {
                string parameterPath = $"{path}.parameters.{supplied.Name}";
                IndicatorParameterDescriptor descriptor = entry.Parameters.FirstOrDefault(d => string.Equals(d.Name, supplied.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolException(
                        ToolErrorCodes.InvalidIndicatorParameter,
                        $"{entry.Type} has no parameter '{supplied.Name}'.",
                        parameterPath,
                        IndicatorParameterRules.ValidNamesHint(entry));

                CoreIndicatorParameterBase target = settings.ParameterObject
                    ?? throw new InvalidOperationException($"{entry.Type} lists parameters but has no parameter object.");
                PropertyInfo property = target.GetType().GetProperty(descriptor.Name)
                    ?? throw new InvalidOperationException($"{target.GetType().Name} has no property {descriptor.Name}.");
                property.SetValue(target, Convert(entry, descriptor, property, supplied.Value, parameterPath));
            }
        }

        if (priceSource is { } source)
        {
            settings.PriceSource = source;
        }

        IndicatorParameterRules.ValidateObject(entry, settings.ParameterObject, path);

        return settings;
    }

    private static object? Convert(IndicatorCatalogEntry entry, IndicatorParameterDescriptor descriptor, PropertyInfo property, JsonElement value, string parameterPath)
    {
        Type clrType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        switch (descriptor.Type)
        {
            case IndicatorParameterReader.IntegerType:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number))
                {
                    throw WrongType(entry, descriptor, "an integer", parameterPath);
                }

                IndicatorParameterRules.CheckBounds(entry, descriptor, number, parameterPath);
                try
                {
                    return System.Convert.ChangeType(number, clrType, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    throw IndicatorParameterRules.OutOfRange(entry, descriptor, parameterPath);
                }
            }

            case IndicatorParameterReader.NumberType:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal number))
                {
                    throw WrongType(entry, descriptor, "a number", parameterPath);
                }

                IndicatorParameterRules.CheckBounds(entry, descriptor, number, parameterPath);
                return System.Convert.ChangeType(number, clrType, CultureInfo.InvariantCulture);
            }

            case IndicatorParameterReader.BooleanType:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : throw WrongType(entry, descriptor, "true or false", parameterPath);

            case IndicatorParameterReader.StringType:
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : throw WrongType(entry, descriptor, "a string", parameterPath);

            case IndicatorParameterReader.EnumType:
            {
                string? name = value.ValueKind == JsonValueKind.String
                    ? descriptor.AllowedValues!.FirstOrDefault(a => string.Equals(a, value.GetString(), StringComparison.OrdinalIgnoreCase))
                    : null;
                return name is null
                    ? throw new ToolException(
                        ToolErrorCodes.InvalidIndicatorParameter,
                        $"Parameter '{descriptor.Name}' of {entry.Type} has an unsupported value.",
                        parameterPath,
                        $"Allowed values: {string.Join(", ", descriptor.AllowedValues!)}.")
                    : Enum.Parse(clrType, name);
            }

            default:
                throw new ToolException(
                    ToolErrorCodes.InvalidIndicatorParameter,
                    $"Parameter '{descriptor.Name}' of {entry.Type} (type {descriptor.Type}) cannot be set through this tool.",
                    parameterPath);
        }
    }

    private static ToolException WrongType(IndicatorCatalogEntry entry, IndicatorParameterDescriptor descriptor, string expected, string parameterPath)
        => new(
            ToolErrorCodes.InvalidIndicatorParameter,
            $"Parameter '{descriptor.Name}' of {entry.Type} must be {expected}.",
            parameterPath);
}
