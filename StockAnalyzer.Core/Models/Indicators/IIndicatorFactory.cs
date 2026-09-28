namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Abstraction for creating ICoreIndicator instances.
/// Enables DI-based indicator resolution and testability via mocking.
/// </summary>
public interface IIndicatorFactory
{
    /// <summary>
    /// Creates an indicator instance for the given type and configures it with the provided parameters.
    /// Returns null if the indicator type is not registered.
    /// </summary>
    ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null);

    /// <summary>
    /// Returns whether a given IndicatorType is registered in the factory.
    /// </summary>
    bool IsRegistered(IndicatorType type);

    /// <summary>
    /// Gets all registered IndicatorTypes.
    /// </summary>
    IEnumerable<IndicatorType> GetRegisteredTypes();
}
