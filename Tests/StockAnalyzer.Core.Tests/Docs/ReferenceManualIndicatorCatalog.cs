using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Docs;

/// <summary>
/// Single machine-readable source for docs/OSS/09_indicator_catalog.md.
/// The catalog rows are reflected from the production code (IndicatorFactory, GetDefaultSettings, parameter classes,
/// calculated series names), so the manual cannot drift from the implementation.
/// Regenerate the generated sections with: SA_REGEN_REFMANUAL=1 dotnet test --filter ReferenceManualIndicatorCatalog
/// </summary>
public sealed class ReferenceManualIndicatorCatalog
{
    private const string RegenEnvVar = "SA_REGEN_REFMANUAL";
    private const string CatalogFileName = "09_indicator_catalog.md";
    private const string GeneratedBeginMarker = "<!-- BEGIN GENERATED CATALOG -->";
    private const string GeneratedEndMarker = "<!-- END GENERATED CATALOG -->";
    private const int SampleBarCount = 400;
    private const int SeriesProbeTimeoutMs = 5000;
    private static readonly Lazy<string> RepoRoot = new(TestRepositoryRoot.Find);

    internal sealed record ParameterRow(string Name, string TypeName, string DefaultText, string? RangeText, bool IsEnum);

    internal sealed record CatalogRow(
        IndicatorType Type,
        int Ordinal,
        string Category,
        string ParameterClass,
        IReadOnlyList<ParameterRow> Parameters,
        bool IsOverlay,
        IReadOnlyList<string> SeriesNames);

    private static string GetCatalogPath()
    {
        string path = Path.Combine(RepoRoot.Value, "docs", "OSS", CatalogFileName);
        return File.Exists(path) ? path : Path.Combine(RepoRoot.Value, "docs", "ReferenceManual", CatalogFileName);
    }

    private static IReadOnlyList<CoreCandleData> BuildSampleCandles()
    {
        var candles = new List<CoreCandleData>(SampleBarCount);
        DateTime start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < SampleBarCount; i++)
        {
            decimal close = 100m + (decimal)(10 * Math.Sin(i / 10.0)) + i * 0.05m;
            decimal open = close - 0.3m;
            candles.Add(new CoreCandleData(start.AddDays(i), open, close + 1m, open - 1m, close, 1_000_000L + i * 1000L));
        }

        return candles;
    }

    private static string FormatTypeName(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        string name = underlying == typeof(int) ? "int"
            : underlying == typeof(double) ? "double"
            : underlying == typeof(decimal) ? "decimal"
            : underlying == typeof(bool) ? "bool"
            : underlying == typeof(string) ? "string"
            : underlying == typeof(long) ? "long"
            : underlying == typeof(float) ? "float"
            : underlying.Name;
        return underlying == type ? name : name + "?";
    }

    private static string FormatDefault(object? value)
    {
        return value switch
        {
            null => "null",
            string s => "\"" + s + "\"",
            bool b => b ? "true" : "false",
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + " (" + e + ")",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
    }

    internal static IReadOnlyList<ParameterRow> ReflectParameters(CoreIndicatorParameterBase parameters)
    {
        var rows = new List<ParameterRow>();
        foreach (PropertyInfo property in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.DeclaringType == typeof(CoreIndicatorParameterBase)
                || property.GetSetMethod() is null
                || property.GetIndexParameters().Length > 0
                || property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
            {
                continue;
            }

            Type underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            string defaultText = FormatDefault(property.GetValue(parameters));
            var range = property.GetCustomAttribute<RangeAttribute>();
            string? rangeText = range is null ? null : $"[{range.Minimum}, {range.Maximum}]";
            rows.Add(new ParameterRow(property.Name, FormatTypeName(property.PropertyType), defaultText, rangeText, underlying.IsEnum));
        }

        return rows;
    }

    private static IReadOnlyList<string> ProbeSeriesNames(IndicatorType type, CoreIndicatorParameterBase? parameters, IReadOnlyList<CoreCandleData> candles)
    {
        try
        {
            var task = Task.Run(() =>
            {
                ICoreIndicator? indicator = IndicatorFactory.Default.Create(type);
                if (indicator is null)
                {
                    return (IReadOnlyList<string>)Array.Empty<string>();
                }

                if (parameters is not null)
                {
                    indicator.Configure(parameters.Clone());
                }

                IIndicatorResult result = indicator.Calculate(candles);
                return (IReadOnlyList<string>)result.SeriesNamesList.ToList();
            });
            return task.Wait(SeriesProbeTimeoutMs) ? task.Result : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    internal static IReadOnlyList<CatalogRow> BuildRows()
    {
        var candles = BuildSampleCandles();
        var rows = new List<CatalogRow>();
        foreach (IndicatorType type in IndicatorFactory.Default.GetRegisteredTypes().OrderBy(t => (int)t))
        {
            ICoreIndicator? indicator = IndicatorFactory.Default.Create(type);
            if (indicator is null)
            {
                continue;
            }

            CoreIndicatorSettings settings = indicator.GetDefaultSettings();
            CoreIndicatorParameterBase? parameters = settings.ParameterObject;
            string parameterClass = parameters?.GetType().Name ?? "(none)";
            IReadOnlyList<ParameterRow> parameterRows = parameters is null ? Array.Empty<ParameterRow>() : ReflectParameters(parameters);
            IReadOnlyList<string> series = ProbeSeriesNames(type, parameters, candles);
            rows.Add(new CatalogRow(type, (int)type, settings.Category.ToString(), parameterClass, parameterRows, settings.IsOverlay, series));
        }

        return rows;
    }

    private static string RenderRow(CatalogRow row)
    {
        string parameters = row.Parameters.Count == 0
            ? "(no parameters)"
            : string.Join(", ", row.Parameters.Select(p => $"`{p.Name}` ({p.TypeName}, default `{p.DefaultText}`" + (p.RangeText is null ? string.Empty : $", range `{p.RangeText}`") + ")"));
        string series = row.SeriesNames.Count == 0
            ? "(not probed)"
            : string.Join(", ", row.SeriesNames.Select(s => $"`\"{s}\"`"));
        return $"| `{row.Type}` | `{row.Ordinal}` | `{row.ParameterClass}` | {parameters} | `{row.IsOverlay.ToString().ToLowerInvariant()}` | {series} |";
    }

    private static string RenderGenerated(IReadOnlyList<CatalogRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 2. Core Indicator Types & Parameter Catalog");
        sb.AppendLine();
        sb.AppendLine("This section is generated from the production code (`IndicatorFactory`, each indicator's default settings, its parameter class and its calculated output series). Every registered indicator is listed. Use the **Ordinal** (or the member name string) for `TypeEnum`/`Type`/`IndicatorType`, the **Parameter Class** as `\"$type\"` and the listed property names verbatim (unknown property names are silently ignored and the default is used). Enum-typed parameter properties are written as integers (see Section 3).");
        sb.AppendLine();
        foreach (IGrouping<string, CatalogRow> group in rows.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"### Category: {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| IndicatorType | Ordinal | Parameter Class (`\"$type\"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (CatalogRow row in group)
            {
                sb.AppendLine(RenderRow(row));
            }

            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 3. Enum Reference for Parameter Properties");
        sb.AppendLine();
        sb.AppendLine("Enums are serialized as integers. The member name in parentheses is for readability only.");
        sb.AppendLine();
        var enumTypes = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        foreach (CatalogRow row in rows)
        {
            ICoreIndicator? indicator = IndicatorFactory.Default.Create(row.Type);
            CoreIndicatorParameterBase? parameters = indicator?.GetDefaultSettings().ParameterObject;
            if (parameters is null)
            {
                continue;
            }

            foreach (PropertyInfo property in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (underlying.IsEnum && property.DeclaringType != typeof(CoreIndicatorParameterBase))
                {
                    enumTypes[underlying.Name] = underlying;
                }
            }
        }

        foreach ((string name, Type enumType) in enumTypes)
        {
            string members = string.Join(", ", Enum.GetValues(enumType).Cast<object>()
                .Select(v => $"`{Convert.ToInt64(v, CultureInfo.InvariantCulture)}` = `{v}`"));
            sb.AppendLine($"- **`{name}`**: {members}");
        }

        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void Doc09_RegenerateCatalog_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable(RegenEnvVar) != "1")
        {
            return;
        }

        string path = GetCatalogPath();
        string doc = File.ReadAllText(path);
        string generated = GeneratedBeginMarker + "\r\n" + RenderGenerated(BuildRows()).Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n" + GeneratedEndMarker;
        var regex = new Regex(Regex.Escape(GeneratedBeginMarker) + @"[\s\S]*?" + Regex.Escape(GeneratedEndMarker));
        Assert.Matches(regex, doc);
        File.WriteAllText(path, regex.Replace(doc, _ => generated));
    }

    [Fact]
    public void Doc09_GeneratedCatalog_IsUpToDateWithCode()
    {
        string doc = File.ReadAllText(GetCatalogPath());
        var regex = new Regex(Regex.Escape(GeneratedBeginMarker) + @"\r?\n([\s\S]*?)\r?\n" + Regex.Escape(GeneratedEndMarker));
        Match match = regex.Match(doc);
        Assert.True(match.Success, "09_indicator_catalog.md must contain the generated catalog markers.");

        string expected = RenderGenerated(BuildRows()).Replace("\r\n", "\n");
        string actual = match.Groups[1].Value.Replace("\r\n", "\n");
        Assert.True(expected == actual,
            $"The generated catalog in {CatalogFileName} is stale. Run: {RegenEnvVar}=1 dotnet test Tests/StockAnalyzer.Core.Tests/StockAnalyzer.Core.Tests.csproj --filter ReferenceManualIndicatorCatalog");
    }
}
