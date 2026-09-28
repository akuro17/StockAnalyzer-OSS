using System.Collections.Concurrent;
using System.Reflection;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Factory for creating ICoreIndicator instances.
/// Uses reflection to discover and register all indicator classes decorated with StockAnalyzerIndicatorAttribute.
/// </summary>
public sealed class IndicatorFactory : IIndicatorFactory
{
    private readonly ConcurrentDictionary<IndicatorType, Type> _registry = new();
    private bool _isInitialized = false;
    private readonly object _lock = new();

    /// <summary>
    /// Default static instance for backward compatibility.
    /// New code should resolve IIndicatorFactory via DI.
    /// </summary>
    public static IIndicatorFactory Default { get; } = new IndicatorFactory();

    public IndicatorFactory()
    {
        Initialize();
    }

    /// <summary>
    /// Initializes the factory by scanning the assembly for indicator implementations.
    /// Thread-safe and idempotent.
    /// </summary>
    private void Initialize()
    {
        if (_isInitialized) return;

        lock (_lock)
        {
            if (_isInitialized) return;

            var assembly = typeof(IndicatorFactory).Assembly;
            var indicatorTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ICoreIndicator).IsAssignableFrom(t));

            foreach (var type in indicatorTypes)
            {
                var attribute = type.GetCustomAttribute<StockAnalyzerIndicatorAttribute>();
                if (attribute != null)
                {
                    _registry[attribute.IndicatorType] = type;
                }
            }

            _isInitialized = true;
        }
    }

    /// <summary>
    /// Returns whether a given IndicatorType is registered in the factory.
    /// </summary>
    public bool IsRegistered(IndicatorType type)
    {
        Initialize();
        return _registry.ContainsKey(type);
    }

    /// <summary>
    /// Gets all registered IndicatorTypes.
    /// </summary>
    public IEnumerable<IndicatorType> GetRegisteredTypes()
    {
        Initialize();
        return _registry.Keys.ToList();
    }

    /// <summary>
    /// Creates an indicator instance for the given type and configures it with the provided parameters.
    /// Returns null if the indicator type is not registered.
    /// </summary>
    public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null)
    {
        Initialize();

        if (!_registry.TryGetValue(type, out var indicatorClass))
        {
            return null;
        }

        var instance = (ICoreIndicator?)Activator.CreateInstance(indicatorClass);
        if (instance != null && parameters != null)
        {
            instance.Configure(parameters);
        }

        return instance;
    }

    // --- Static Fallbacks for Backward Compatibility ---

    public static bool IsRegisteredStatic(IndicatorType type) => Default.IsRegistered(type);
    public static IEnumerable<IndicatorType> GetRegisteredTypesStatic() => Default.GetRegisteredTypes();
    public static ICoreIndicator? CreateStatic(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => Default.Create(type, parameters);
    
    // Kept for existing test compatibility 
    public static void InitializeStatic() { /* No-op, constructor handles it */ }

    /// <summary>
    /// Clears the registry. Primarily for testing purposes.
    /// </summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _registry.Clear();
            _isInitialized = false;
        }
    }
}
