using System.Reflection;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer.Tools;
using static StockAnalyzer.McpServer.Catalog.ConditionJson;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>An indicator side after its checks: the type, the output series and the settings with the client's parameters applied.</summary>
internal sealed record ResolvedIndicatorSide(IndicatorType Type, string Series, CoreIndicatorSettings Settings);

/// <summary>
/// The rules for one indicator side of a condition, shared by the Screener and the Backtest binders: the type exists and can be
/// calculated, it compares one symbol only, the output series exists and is usable bar by bar, and the parameters pass the strict
/// <see cref="IndicatorSettingsBinder"/>. <c>usage</c> names the consumer in messages ("the screener", "a backtest").
/// </summary>
internal sealed class IndicatorSideResolver
{
    private readonly IndicatorCatalog _indicators;
    private readonly IndicatorSettingsBinder _binder;

    public IndicatorSideResolver(IndicatorCatalog indicators, IndicatorSettingsBinder binder)
    {
        _indicators = indicators;
        _binder = binder;
    }

    /// <param name="side">The side object; its optional <c>outputSeriesName</c> and <c>parameters</c> are read here.</param>
    /// <param name="path">The path of the side, for error parameters.</param>
    /// <param name="allowCrossTicker">True for a consumer that supplies the comparison symbol's bars (the Screener); the caller then checks the
    /// <c>ComparisonSymbol</c> parameter. False (the Backtest) rejects an indicator that compares two symbols.</param>
    public ResolvedIndicatorSide Resolve(JsonElement side, string typeName, string usage, string path, bool allowCrossTicker = false)
    {
        string typePath = $"{path}.indicator";
        IndicatorCatalogEntry entry = _indicators.Find(typeName)
            ?? throw new ToolException(ToolErrorCodes.UnknownIndicator, $"Unknown indicator type '{typeName}'.", typePath, "Call sa_get_capabilities to list the type names.");
        if (!entry.IsRegistered)
        {
            throw new ToolException(ToolErrorCodes.UnknownIndicator, $"Indicator type '{entry.Type}' is defined but cannot be calculated.", typePath, "Types with isRegistered false in sa_get_capabilities are not available.");
        }

        if (entry.IsCrossTicker && !allowCrossTicker)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{entry.Type} compares two symbols and cannot be used in {usage}.", typePath);
        }

        IndicatorType type = Enum.Parse<IndicatorType>(entry.Type);
        string[] usableSeries = entry.OutputSeriesNames.Where(series => !entry.NonCausalSeries.Contains(series, StringComparer.Ordinal)).ToArray();
        string? requested = side.TryGetProperty("outputSeriesName", out _)
            ? Require(side, "outputSeriesName", path, JsonValueKind.String).GetString()
            : null;
        string series;
        if (requested is null)
        {
            series = usableSeries.FirstOrDefault()
                ?? throw new ToolException(ToolErrorCodes.InvalidArgument, $"{entry.Type} cannot be used in {usage} ({entry.BacktestEligibility}).", typePath);
        }
        else
        {
            series = entry.OutputSeriesNames.FirstOrDefault(s => string.Equals(s, requested.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException(
                    ToolErrorCodes.InvalidOutputSeries,
                    $"{entry.Type} has no output series '{requested}'.",
                    $"{path}.outputSeriesName",
                    $"Series of {entry.Type}: {string.Join(", ", entry.OutputSeriesNames)}.");
        }

        BacktestIndicatorViolationReason reason = BacktestIndicatorEligibility.Check(type, series);
        if (reason != BacktestIndicatorViolationReason.None)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{entry.Type} ({series}) cannot be used in {usage}: {reason}.",
                typePath,
                reason == BacktestIndicatorViolationReason.NonCausal
                    ? $"The value at a bar depends on later bars. Usable series of {entry.Type}: {(usableSeries.Length == 0 ? "none" : string.Join(", ", usableSeries))}."
                    : "The indicator cannot be calculated bar by bar.");
        }

        side.TryGetProperty("parameters", out JsonElement parameters);
        return new ResolvedIndicatorSide(type, series, _binder.Bind(entry, parameters, null, path));
    }

    /// <summary>
    /// Every readable property of the parameter object by name, as the Screener tab stores an indicator's parameters. The tab's own helper
    /// lives in a view model this project does not reference; the rule is the same (public instance properties, non-null values).
    /// </summary>
    public static Dictionary<string, object> ParameterValues(CoreIndicatorParameterBase? parameterObject)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (parameterObject is null)
        {
            return values;
        }

        foreach (PropertyInfo property in parameterObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0 && property.GetValue(parameterObject) is { } value)
            {
                values[property.Name] = value;
            }
        }

        return values;
    }
}
