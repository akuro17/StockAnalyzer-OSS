using System;
using System.Collections.Generic;
using System.Linq;

using System.Collections.ObjectModel;

namespace StockAnalyzer.Avalonia.Drawing.Serialization;

/// <summary>
/// Discovers all non-abstract IChartObject implementations in the Avalonia assembly once,
/// keyed by CLR type name, so persistence code can resolve a concrete type from a JSON
/// "$type" discriminator without a hand-maintained switch statement (mirrors
/// StockAnalyzer.Core.Serialization.WorkspacePolymorphicResolver's discovery approach).
/// </summary>
internal static class ChartObjectTypeRegistry
{
    private static readonly Dictionary<string, Type> _typesByName;
    private static readonly ReadOnlyDictionary<string, Type> _readOnlyView;

    static ChartObjectTypeRegistry()
    {
        _typesByName = typeof(IChartObject).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IChartObject).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, t => t);
        _readOnlyView = new ReadOnlyDictionary<string, Type>(_typesByName);
    }

    public static Type? Resolve(string typeName)
        => _typesByName.TryGetValue(typeName, out var type) ? type : null;

    internal static IReadOnlyDictionary<string, Type> GetRegisteredTypes()
        => _readOnlyView;
}
