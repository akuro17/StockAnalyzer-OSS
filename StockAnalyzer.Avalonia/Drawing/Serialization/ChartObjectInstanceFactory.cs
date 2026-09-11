using System;
using System.Linq;
using System.Reflection;

namespace StockAnalyzer.Avalonia.Drawing.Serialization;

/// <summary>
/// Instantiates any concrete IChartObject type without requiring a parameterless constructor.
/// IChartObject implementers use a variety of constructor shapes (0-3 ChartPoint args, an
/// optional trailing bool/string, or a List&lt;ChartPoint&gt;) because they were authored for
/// interactive drawing, not for persistence. Rather than adding a parameterless constructor to
/// every one of the ~40 implementers, this factory invokes each type's real constructor with
/// generic placeholder arguments (so constructor-time side effects such as cached SKPaint
/// allocation still run correctly) and lets the caller overwrite every property afterward with
/// real deserialized data.
/// </summary>
internal static class ChartObjectInstanceFactory
{
    public static IChartObject Create(Type concreteType)
    {
        var ctor = concreteType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{concreteType.Name} has no public constructor usable for deserialization.");

        var args = ctor.GetParameters()
            .Select(p => BuildPlaceholder(p.ParameterType))
            .ToArray();

        return (IChartObject)ctor.Invoke(args);
    }

    private static object? BuildPlaceholder(Type parameterType)
    {
        // Covers ChartPoint, bool, int, double, enums, etc.
        if (parameterType.IsValueType) return Activator.CreateInstance(parameterType);
        if (parameterType == typeof(string)) return string.Empty;

        // Covers List<ChartPoint> and similar reference types with a parameterless constructor.
        var parameterlessCtor = parameterType.GetConstructor(Type.EmptyTypes);
        return parameterlessCtor != null ? Activator.CreateInstance(parameterType) : null;
    }
}
