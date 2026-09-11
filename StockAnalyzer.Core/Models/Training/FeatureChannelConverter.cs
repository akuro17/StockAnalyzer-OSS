using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// The single place that marshals an indicator <see cref="FeatureChannel"/>'s string-keyed
/// <see cref="FeatureChannel.Params"/> to and from a live <see cref="CoreIndicatorParameterBase"/>.
/// Both the training-wizard feature picker and the offline feature exporter go through here, so
/// indicator-parameter handling for <see cref="PredictionFeatureMode.ComposedFeatures"/> is defined
/// exactly once (CLAUDE.md: duplicate shared definitions across layers are prohibited).
///
/// <para>
/// Only scalar parameters are marshalled: <see cref="string"/>, <see cref="bool"/>, the common
/// integer and floating-point types, <see cref="decimal"/>, and enums (plus their
/// <see cref="Nullable{T}"/> forms). All parsing and formatting uses
/// <see cref="CultureInfo.InvariantCulture"/> so the wire form is culture-stable.
/// </para>
/// </summary>
public static class FeatureChannelConverter
{
    /// <summary>
    /// Builds a fully configured <see cref="CoreIndicatorSettings"/> for an
    /// <see cref="FeatureChannelKind.Indicator"/> channel: the registry default for the channel's
    /// <see cref="FeatureChannel.Indicator"/> type, with <see cref="FeatureChannel.Params"/> applied
    /// over it. <paramref name="warnings"/> surfaces <see cref="ApplyParams"/>'s unknown-key warnings,
    /// so a caller can tell a stored <see cref="FeatureChannel"/> apart from one whose
    /// <see cref="FeatureChannel.Params"/> carry a key the current registry no longer (or does not yet)
    /// recognize.
    /// </summary>
    /// <exception cref="ArgumentException">The channel is not a valid indicator channel.</exception>
    /// <exception cref="InvalidOperationException">The indicator type is not registered, or a param is unassignable.</exception>
    public static CoreIndicatorSettings BuildIndicatorSettings(FeatureChannel channel, IIndicatorFactory factory, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(factory);

        if (channel.Kind != FeatureChannelKind.Indicator || channel.Indicator is not { } type)
        {
            throw new ArgumentException("FeatureChannel is not an indicator channel.", nameof(channel));
        }

        var indicator = factory.Create(type)
            ?? throw new InvalidOperationException($"Indicator type '{type}' is not registered in the factory.");

        var settings = indicator.GetDefaultSettings();
        settings.TypeEnum = type;
        warnings = ApplyParams(settings.ParameterObject, channel.Params);
        return settings;
    }

    /// <summary>
    /// Extracts the parameters of <paramref name="configured"/> that differ from the registry default
    /// for its <see cref="CoreIndicatorSettings.TypeEnum"/>, as an invariant-culture string map
    /// suitable for <see cref="FeatureChannel.Params"/>. Parameters left at their default are omitted
    /// so the wire form stays minimal.
    /// <para>
    /// Constraint: a scalar property whose current value is <see langword="null"/> (only possible for
    /// a <see cref="Nullable{T}"/> scalar) is skipped entirely, regardless of the registry default -
    /// there is currently no indicator parameter class with a nullable scalar property, so this path is
    /// unreachable with real data today. If one is ever added, an explicit "reset to null" edit would
    /// not be captured as a diff here and would round-trip back to the registry default instead.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ExtractParams(CoreIndicatorSettings configured, IIndicatorFactory factory)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(factory);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configured.ParameterObject is null || configured.TypeEnum is not { } type)
        {
            return result;
        }

        var defaults = factory.Create(type)?.GetDefaultSettings()?.ParameterObject;

        foreach (var prop in ScalarProperties(configured.ParameterObject.GetType()))
        {
            var current = prop.GetValue(configured.ParameterObject);
            if (current is null)
            {
                continue;
            }

            var defaultValue = defaults is null ? null : prop.GetValue(defaults);
            if (!Equals(current, defaultValue))
            {
                result[prop.Name] = FormatInvariant(current);
            }
        }

        return result;
    }

    /// <summary>
    /// Applies <paramref name="values"/> onto <paramref name="target"/> by (case-insensitive)
    /// property name. A value that cannot be converted to the property type throws (data corruption,
    /// not a compatibility concern). An unknown or read-only name is still skipped rather than thrown -
    /// this keeps loading forward-compatible with a <see cref="FeatureChannel"/> saved by a newer build
    /// - but is reported back as a warning so the caller can surface it instead of losing it silently.
    /// </summary>
    /// <returns>One message per unknown/unwritable key that was skipped; empty when every key applied.</returns>
    public static IReadOnlyList<string> ApplyParams(CoreIndicatorParameterBase? target, IReadOnlyDictionary<string, string>? values)
    {
        var warnings = new List<string>();
        if (target is null || values is null || values.Count == 0)
        {
            return warnings;
        }

        var props = ScalarProperties(target.GetType())
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, raw) in values)
        {
            if (!props.TryGetValue(name, out var prop))
            {
                warnings.Add($"Unknown parameter '{name}' for {target.GetType().Name} was ignored.");
                continue;
            }

            if (!TryParseInvariant(raw, prop.PropertyType, out var parsed))
            {
                throw new InvalidOperationException(
                    $"FeatureChannel param '{name}' = '{raw}' is not assignable to {prop.PropertyType.Name}.");
            }

            prop.SetValue(target, parsed);
        }

        return warnings;
    }

    /// <summary>
    /// Builds the display label used everywhere an indicator channel's short name and period-like
    /// parameters are shown (Add flow, in-place "Selected" edit, and template reload/preview) - the
    /// single formatting rule so the three call sites cannot drift apart. Unlike <see cref="ExtractParams"/>
    /// (which records only the diff from the registry default, for the wire form), this always lists
    /// the CURRENT value of every numeric scalar parameter - so a freshly added channel still sitting
    /// at its registry default reads identically to one the user has edited. Non-numeric parameters
    /// (bool/enum/string, e.g. a moving-average-type selector) are intentionally omitted: the label is
    /// a compact "name (period-like values)" summary, not a full settings dump.
    /// </summary>
    /// <returns><c>"ShortName"</c> when <paramref name="parameterObject"/> is <see langword="null"/> or
    /// has no numeric scalar property; otherwise <c>"ShortName (v1, v2, ...)"</c> in property
    /// declaration order.</returns>
    public static string BuildIndicatorLabel(string shortName, CoreIndicatorParameterBase? parameterObject)
    {
        ArgumentNullException.ThrowIfNull(shortName);

        if (parameterObject is null)
        {
            return shortName;
        }

        var values = ScalarProperties(parameterObject.GetType())
            .Where(p => IsNumericScalar(p.PropertyType))
            .Select(p => p.GetValue(parameterObject))
            .Where(v => v is not null)
            .Select(v => FormatInvariant(v!))
            .ToList();

        return values.Count > 0
            ? shortName + " (" + string.Join(", ", values) + ")"
            : shortName;
    }

    private static bool IsNumericScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(int)
            || t == typeof(long)
            || t == typeof(short)
            || t == typeof(byte)
            || t == typeof(double)
            || t == typeof(float)
            || t == typeof(decimal);
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ScalarPropertyCache = new();

    private static PropertyInfo[] ScalarProperties(Type type)
        => ScalarPropertyCache.GetOrAdd(
            type,
            static t => Array.FindAll(
                t.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p => p.GetIndexParameters().Length == 0 && IsScalar(p.PropertyType)));

    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsEnum
            || t == typeof(string)
            || t == typeof(bool)
            || t == typeof(int)
            || t == typeof(long)
            || t == typeof(short)
            || t == typeof(byte)
            || t == typeof(double)
            || t == typeof(float)
            || t == typeof(decimal);
    }

    private static string FormatInvariant(object value)
        => value switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static bool TryParseInvariant(string raw, Type targetType, out object? value)
    {
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (Nullable.GetUnderlyingType(targetType) is not null && string.IsNullOrWhiteSpace(raw))
        {
            value = null;
            return true;
        }

        try
        {
            if (t.IsEnum)
            {
                value = Enum.Parse(t, raw.Trim(), ignoreCase: true);
                return Enum.IsDefined(t, value);
            }

            if (t == typeof(string)) { value = raw; return true; }
            if (t == typeof(bool)) { value = bool.Parse(raw.Trim()); return true; }
            if (t == typeof(int)) { value = int.Parse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture); return true; }
            if (t == typeof(long)) { value = long.Parse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture); return true; }
            if (t == typeof(short)) { value = short.Parse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture); return true; }
            if (t == typeof(byte)) { value = byte.Parse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture); return true; }
            if (t == typeof(double)) { value = double.Parse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture); return true; }
            if (t == typeof(float)) { value = float.Parse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture); return true; }
            if (t == typeof(decimal)) { value = decimal.Parse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture); return true; }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            value = null;
            return false;
        }

        value = null;
        return false;
    }
}
