using System.Globalization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// The one definition of the checks an indicator parameter must pass before a tool uses it: the [CoreParameterRange] bounds of the catalog
/// descriptor, the parameter object's own <c>Validate</c>, and the hint that names the valid parameters. Used by <see cref="IndicatorSettingsBinder"/>
/// for the client's JSON values and by <see cref="BacktestConditionBinder"/> for the typed parameters of a saved template, so every route reports
/// the same code and message for the same violation. Only <c>param</c> and <c>hint</c> differ: a template violation names the template as
/// <c>param</c> and its position inside the template in <c>hint</c> (the <c>location</c> arguments).
/// </summary>
internal static class IndicatorParameterRules
{
    /// <summary>Throws <see cref="ToolErrorCodes.InvalidIndicatorParameter"/> when <paramref name="value"/> is outside the descriptor's [CoreParameterRange].</summary>
    /// <param name="location">Position of the value inside a saved template (for example <c>entryLong.children[0].left.parameters.Period</c>), or null for a client value.</param>
    public static void CheckBounds(IndicatorCatalogEntry entry, IndicatorParameterDescriptor descriptor, decimal value, string parameterPath, string? location = null)
    {
        if ((descriptor.Min is { } min && value < min) || (descriptor.Max is { } max && value > max))
        {
            throw OutOfRange(entry, descriptor, parameterPath, location);
        }
    }

    public static ToolException OutOfRange(IndicatorCatalogEntry entry, IndicatorParameterDescriptor descriptor, string parameterPath, string? location = null)
        => new(
            ToolErrorCodes.InvalidIndicatorParameter,
            $"Parameter '{descriptor.Name}' of {entry.Type} is outside its range {descriptor.Min?.ToString(CultureInfo.InvariantCulture)}..{descriptor.Max?.ToString(CultureInfo.InvariantCulture)}.",
            parameterPath,
            WithLocation(location, descriptor.GuiMax is null ? null : $"The chart input accepts {descriptor.GuiMin?.ToString(CultureInfo.InvariantCulture)}..{descriptor.GuiMax?.ToString(CultureInfo.InvariantCulture)}."));

    /// <summary>
    /// Runs the parameter object's own <c>Validate</c>; a violation is reported as <see cref="ToolErrorCodes.InvalidIndicatorParameter"/> at
    /// <c>path.parameters[.Name]</c>. For a saved template (<paramref name="sideLocation"/> not null) <c>param</c> is <paramref name="path"/> itself and
    /// the position is in the hint.
    /// </summary>
    public static void ValidateObject(IndicatorCatalogEntry entry, CoreIndicatorParameterBase? parameters, string path, string? sideLocation = null)
    {
        try
        {
            parameters?.Validate();
        }
        catch (ArgumentException error)
        {
            string name = error.ParamName is null ? string.Empty : $".{error.ParamName}";
            throw new ToolException(
                ToolErrorCodes.InvalidIndicatorParameter,
                $"The parameters of {entry.Type} are not valid{(error.ParamName is null ? string.Empty : $" ('{error.ParamName}')")}.",
                sideLocation is null ? $"{path}.parameters{name}" : path,
                WithLocation(sideLocation is null ? null : $"{sideLocation}.parameters{name}", ValidNamesHint(entry)));
        }
    }

    /// <summary>
    /// Checks the typed parameters of a saved template the way the client's JSON values are checked: every numeric [CoreParameterRange], then the
    /// object's own <c>Validate</c>. The scope is exactly that: an integer or number parameter that has a Min or a Max is compared with it;
    /// a parameter without a bound, a non-numeric parameter (for example an enum or a flag) and a descriptor with no matching property are
    /// skipped; only an <see cref="ArgumentException"/> from <c>Validate</c> becomes <see cref="ToolErrorCodes.InvalidIndicatorParameter"/>
    /// (documented in docs/SA_AI_INTEGRATION.md, error code INVALID_INDICATOR_PARAMETER).
    /// </summary>
    /// <param name="path">The argument that names the template (reported as <c>param</c>).</param>
    /// <param name="sideLocation">Position of the side inside the template, for example <c>entryLong.children[0].left</c>.</param>
    public static void CheckTyped(IndicatorCatalogEntry entry, CoreIndicatorParameterBase parameters, string path, string sideLocation)
    {
        foreach (IndicatorParameterDescriptor descriptor in entry.Parameters)
        {
            if ((descriptor.Min is null && descriptor.Max is null)
                || descriptor.Type is not (IndicatorParameterReader.IntegerType or IndicatorParameterReader.NumberType)
                || parameters.GetType().GetProperty(descriptor.Name) is not { } property
                || property.GetValue(parameters) is not { } raw)
            {
                continue;
            }

            string location = $"{sideLocation}.parameters.{descriptor.Name}";
            decimal value;
            try
            {
                value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception error) when (error is OverflowException or FormatException or InvalidCastException)
            {
                // NaN, infinity or a value beyond decimal cannot lie inside any range.
                throw OutOfRange(entry, descriptor, path, location);
            }

            CheckBounds(entry, descriptor, value, path, location);
        }

        ValidateObject(entry, parameters, path, sideLocation);
    }

    public static string ValidNamesHint(IndicatorCatalogEntry entry)
        => entry.Parameters.Count == 0
            ? $"{entry.Type} has no parameters."
            : $"Parameters of {entry.Type}: {string.Join(", ", entry.Parameters.Select(p => p.Name))}. Call sa_get_capabilities with indicatorTypeFilter for ranges.";

    private static string? WithLocation(string? location, string? hint)
        => location is null ? hint : hint is null ? location : $"{location}. {hint}";
}
