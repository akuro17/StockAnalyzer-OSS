using StockAnalyzer.Core.Optimization;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Constants;
using System;
using System.Reflection;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.Confluence;
using System.Collections.Concurrent;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Base class for all indicators.
/// Subclasses should override CalculateCore to perform the actual calculation.
/// </summary>
public abstract class CoreIndicatorBase : ICoreIndicator
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertyCache = new();
    
    public abstract string Name { get; }
    protected List<decimal?> _values = new();
    public IReadOnlyList<decimal?> Values => _values;
    
    /// <summary>
    /// Reusable signal provider for indicators that follow the standard naming convention
    /// for signal series (e.g., BullishSignals, BearishSignals).
    /// </summary>
    protected StandardConfluenceSignalProvider? _signalProvider;
    
    /// <summary>
    /// Context used for the current calculation execution.
    /// Used to pass optimization parameters (e.g. parallelism threshold) to internal methods.
    /// This state is transient and valid only during Calculate execution.
    /// </summary>
    protected OptimizationContext? _executionOptimizationContext;

    /// <summary>
    /// Gets a value indicating whether this indicator should be drawn as an overlay on the main chart (true)
    /// or in a separate panel (false).
    /// Defaults to true.
    /// </summary>
    public virtual bool IsOverlay => true;


    /// <summary>
    /// Configures the indicator with the given parameters.
    /// Default implementation does nothing. Override in derived classes to accept specific parameter types.
    /// </summary>
    public virtual void Configure(CoreIndicatorParameterBase parameters)
    {
        // Default: no configuration needed. Subclasses override as needed.
    }

    /// <summary>
    /// Gets the default settings for this indicator. 
    /// This uses reflection to infer the IndicatorType and the default parameter object based on naming conventions.
    /// Override in derived classes for custom default settings.
    /// </summary>
    public virtual CoreIndicatorSettings GetDefaultSettings()
    {
        var settings = new CoreIndicatorSettings
        {
            TypeEnum = null,
            IsEnabled = false,
            Category = CoreIndicatorCategory.Other,
            Color = IndicatorDefaultConstants.Gray, // Default gray
            Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
            Style = CoreLineStyle.Solid,
            IsOverlay = this.IsOverlay,
            ParameterObject = null
        };

        // 1. Auto-discover Category and IsOverlay based on namespace
        var ns = GetType().Namespace ?? string.Empty;
        if (ns.EndsWith(".Oscillators"))
        {
            settings.Category = CoreIndicatorCategory.Oscillator;
            settings.IsOverlay = false;
        }
        else if (ns.EndsWith(".Volume"))
        {
            settings.Category = CoreIndicatorCategory.Volume;
            settings.IsOverlay = false;
        }
        else if (ns.EndsWith(".Volatility"))
        {
            settings.Category = CoreIndicatorCategory.Volatility;
            settings.IsOverlay = false;
        }
        else if (ns.EndsWith(".Trend") || ns.EndsWith(".MovingAverages"))
        {
            settings.Category = CoreIndicatorCategory.Trend;
            settings.IsOverlay = true;
        }
        else if (ns.EndsWith(".Chart"))
        {
            settings.Category = CoreIndicatorCategory.Chart;
            settings.IsOverlay = true;
        }
        else if (ns.EndsWith(".Mathematical"))
        {
            settings.Category = CoreIndicatorCategory.Math;
            settings.IsOverlay = true;
        }
        else if (ns.EndsWith(".Advanced"))
        {
            settings.Category = CoreIndicatorCategory.Other;
            settings.IsOverlay = false;
        }

        // If the specific indicator explicitly overrode IsOverlay, respect it over the namespace default
        bool isOverlayOverridden = GetType().GetProperty(nameof(IsOverlay))?.DeclaringType != typeof(CoreIndicatorBase);
        if (isOverlayOverridden)
        {
            settings.IsOverlay = this.IsOverlay;
        }
        else
        {
            // 2. Auto-discover IsOverlay via Mock Calculation
            try
            {
                var mockCandles = GetMockCandles();
                var testInst = (CoreIndicatorBase)Activator.CreateInstance(GetType())!;
                
                // 3. Try to auto-discover parameters via naming convention
                string expectedParamName = GetType().Name.Replace("Indicator", "Parameter");
                var paramType = GetType().Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == expectedParamName && typeof(CoreIndicatorParameterBase).IsAssignableFrom(t));

                if (paramType != null)
                {
                    settings.ParameterObject = (CoreIndicatorParameterBase?)Activator.CreateInstance(paramType);
                    if (settings.ParameterObject != null)
                    {
                        testInst.Configure(settings.ParameterObject);
                    }
                }

                var result = testInst.Calculate(mockCandles);
                if (result.IsSuccessful && testInst.Values.Any(v => v.HasValue))
                {
                    var validValues = testInst.Values.Where(v => v.HasValue).Select(v => Math.Abs(v!.Value)).ToList();
                    if (validValues.Count > 0)
                    {
                        decimal avg = validValues.Average();
                        // 10000m is the mock base price. 
                        // If average is > 2000 and < 50000, it tracks price scale (meaning it's an overlay)
                        if (avg > 2000m && avg < 50000m)
                        {
                            settings.IsOverlay = true;
                        }
                        else
                        {
                            settings.IsOverlay = false;
                        }
                    }
                }
            }
            catch
            {
                // Ignore calculation errors and fall back to namespace defaults
            }
        }

        // 4. Try to determine TypeEnum from attribute
        var attr = (StockAnalyzerIndicatorAttribute?)Attribute.GetCustomAttribute(GetType(), typeof(StockAnalyzerIndicatorAttribute));
        if (attr != null)
        {
            settings.TypeEnum = attr.IndicatorType;
        }

        // Parameter discovery was moved up to testInst configuration
        if (settings.ParameterObject == null)
        {
            string expectedParamName = GetType().Name.Replace("Indicator", "Parameter");
            var paramType = GetType().Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == expectedParamName && typeof(CoreIndicatorParameterBase).IsAssignableFrom(t));

            if (paramType != null)
            {
                try
                {
                    settings.ParameterObject = (CoreIndicatorParameterBase?)Activator.CreateInstance(paramType);
                }
                catch { }
            }
        }

        // Assign category-appropriate default color instead of gray fallback.
        // This ensures indicators without explicit DefaultCoreIndicatorSettings entries
        // get visually meaningful colors instead of #808080.
        settings.Color = settings.Category switch
        {
            CoreIndicatorCategory.Trend => IndicatorDefaultConstants.SmaColor,          // DodgerBlue #1E90FF
            CoreIndicatorCategory.Oscillator => IndicatorDefaultConstants.RsiColor,      // Purple #AB47BC
            CoreIndicatorCategory.Volume => IndicatorDefaultConstants.Cyan,       // Cyan #00ACC1
            CoreIndicatorCategory.Volatility => IndicatorDefaultConstants.RsiColor,     // Purple #AB47BC
            CoreIndicatorCategory.Chart => IndicatorDefaultConstants.SmaColor,          // DodgerBlue #1E90FF
            CoreIndicatorCategory.Math => IndicatorDefaultConstants.SmaColor,           // DodgerBlue #1E90FF
            _ => IndicatorDefaultConstants.RsiColor                                     // Purple #AB47BC (Advanced/Other → sub-window)
        };

        // Also set appropriate thickness for sub-panel indicators
        if (!settings.IsOverlay)
        {
            settings.Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness;
        }

        return settings;
    }

    private static List<CoreCandleData>? _mockCandles;
    private static List<CoreCandleData> GetMockCandles()
    {
        if (_mockCandles != null) return _mockCandles;
        
        var mockCandles = new List<CoreCandleData>();
        decimal currentPrice = 10000m; // Base price for mock calculation (to distinguish from 0-100 oscillators)
        var random = new Random(12345); // Deterministic random seed
        var startDate = new DateTime(2020, 1, 1);
        
        for (int i = 0; i < 500; i++)
        {
            decimal change = (decimal)(random.NextDouble() * 100 - 50); // +/- 50 price change
            currentPrice += change;
            mockCandles.Add(new CoreCandleData(
                Timestamp: startDate.AddDays(i),
                Open: currentPrice - 20,
                High: currentPrice + 50,
                Low: currentPrice - 50,
                Close: currentPrice,
                Volume: (long)(1000000 + random.NextDouble() * 500000)
            ));
        }
        
        return _mockCandles = mockCandles;
    }

    /// <summary>
    /// Calculates the indicator with a specific optimization context.
    /// </summary>
    public IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles, OptimizationContext context)
    {
        _executionOptimizationContext = context;
        try
        {
            return Calculate(candles);
        }
        finally
        {
            _executionOptimizationContext = null;
        }
    }

    /// <summary>
    /// Calculates the indicator and returns the result.
    /// Wraps CalculateCore with error handling.
    /// </summary>
    public IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        
        if (candles == null)
        {
            return IndicatorResult.Failure("Candles data is null.");
        }

        if (candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        try
        {
            return CalculateCore(candles);
        }
        catch (Exception ex)
        {
            return IndicatorResult.Failure($"Calculation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Calculates the indicator values asynchronously using the provided execution context.
    /// Base implementation wraps the synchronous Calculate method.
    /// Override this in subclasses that require true async execution.
    /// </summary>
    public virtual Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        // Default to synchronous calculation
        return Task.FromResult(Calculate(candles));
    }

    /// <summary>
    /// Override in derived classes to perform the actual calculation.
    /// Should populate _values and return an appropriate IIndicatorResult.
    /// </summary>
    protected abstract IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles);

    /// <summary>
    /// Calculates the indicator values/objects in parallel by partitioning the data indices.
    /// Incorporates a sequential fallback for small datasets to avoid overhead.
    /// </summary>
    /// <typeparam name="TResult">The type of the result (e.g., decimal?).</typeparam>
    /// <param name="candles">The source candle data.</param>
    /// <param name="calculateSegment">
    /// A delegate responsible for calculating a range of values.
    /// Arguments: (SourceCandles, StartIndex, EndIndex).
    /// Returns: List of results for the specified range [StartIndex, EndIndex).
    /// </param>
    /// <param name="context">Optional optimization context. If null, a default Balanced context is used.</param>
    /// <returns>A list of results synchronized with the input candles order.</returns>
    protected IReadOnlyList<TResult> CalculateParallel<TResult>(
        IReadOnlyList<CoreCandleData> candles,
        Func<IReadOnlyList<CoreCandleData>, int, int, List<TResult>> calculateSegment,
        OptimizationContext? context = null)
    {
        // 1. Parameter Validation
        if (candles == null || candles.Count == 0)
        {
            return new List<TResult>();
        }

        // 2. Optimization Context
        // Use provided context, or stored execution context, or default.
        var ctx = context ?? _executionOptimizationContext ?? new OptimizationContext();

        // 1. Check Threshold
        // If data count is small, parallel overhead outweighs benefits.
        // Used dynamic threshold from OptimizationContext.
        if (candles.Count < ctx.ParallelizationThreshold)
        {
            return calculateSegment(candles, 0, candles.Count);
        }

        // 4. Parallel Execution Wrapper
        // We use GetAwaiter().GetResult() to bridge to the async Partitioner 
        // because the base ICoreIndicator interface is currently synchronous.
        try
        {
            return CalculateParallelAsync(candles, calculateSegment, ctx).GetAwaiter().GetResult();
        }
        catch (AggregateException ae)
        {
            // Unwrap and throw the first exception to mimic synchronous behavior
            throw ae.InnerException ?? ae;
        }
    }

    /// <summary>
    /// Asynchronous version of CalculateParallel.
    /// </summary>
    protected async Task<IReadOnlyList<TResult>> CalculateParallelAsync<TResult>(
        IReadOnlyList<CoreCandleData> candles,
        Func<IReadOnlyList<CoreCandleData>, int, int, List<TResult>> calculateSegment,
        OptimizationContext context)
    {
        // Define the source as the list of indices
        var indices = Enumerable.Range(0, candles.Count).ToList();

        // Execute via WorkloadPartitioner
        // Current WorkloadPartitioner returns IList<TResult> without guaranteed order.
        // To preserve order, we must tag results with their chunk index or start index.
        
        var taggedResults = await WorkloadPartitioner.ExecuteParallelAsync(
            indices,
            context,
            (chunkIndices) =>
            {
                if (chunkIndices.Count == 0) return Enumerable.Empty<(int StartIndex, List<TResult> Values)>();

                // Since chunkIndices is contiguous from WorkloadPartitioner
                int start = chunkIndices[0];
                int count = chunkIndices.Count;
                int end = start + count;

                // Execute the segment calculation
                var segmentResults = calculateSegment(candles, start, end);

                // Return as a single tagged item (chunk start index, values)
                // We wrap it in an array because generic return is IEnumerable
                return new[] { (StartIndex: start, Values: segmentResults) };
            }
        ).ConfigureAwait(false);

        // Sort by StartIndex and Flatten
        // Implementation Note: Since concurrent execution might return chunks in any order,
        // we sort them to reconstruct the continuous time series.
        var finalResults = taggedResults
            .OrderBy(x => x.StartIndex)
            .SelectMany(x => x.Values)
            .ToList();

        return finalResults;
    }

    /// <summary>
    /// Automatically creates an IIndicatorResult by reflectively discovering public properties
    /// that are of type IEnumerable<decimal?>.
    /// </summary>
    /// <returns>IIndicatorResult with all discovered series.</returns>
    protected IIndicatorResult CreateAutomaticResult()
    {
        var series = new Dictionary<string, IReadOnlyList<decimal?>>();

        // Add the main series (defaults to _values)
        if (_values != null)
        {
            series[IndicatorResult.MainSeriesName] = _values;
        }

        // Get all public instance properties (Cached to avoid heap allocation on every calculation)
        var type = GetType();
        if (!_propertyCache.TryGetValue(type, out var properties))
        {
            properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            _propertyCache.TryAdd(type, properties);
        }

        foreach (var prop in properties)
        {
            // Skip if marked with [IndicatorResultIgnore]
            if (Attribute.IsDefined(prop, typeof(StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnoreAttribute)))
            {
                continue;
            }

            // Skip the "Values" property itself to avoid duplication
            if (prop.Name == nameof(Values))
            {
                continue;
            }

            // Check if property is IEnumerable<decimal?>
            if (typeof(IEnumerable<decimal?>).IsAssignableFrom(prop.PropertyType))
            {
                var value = prop.GetValue(this) as IEnumerable<decimal?>;
                if (value != null)
                {
                    // Convert to IReadOnlyList if needed
                    var list = value as IReadOnlyList<decimal?> ?? value.ToList();
                    series[prop.Name] = list;
                }
            }

            // Automatic Signal Discovery (Phase 3: Logic Integration)
            // If the provider hasn't been manually configured, we look for standard naming conventions.
            if (_signalProvider == null || _signalProvider.SeriesCount == 0)
            {
                if (prop.Name == "BullishSignals" || prop.Name == "BuySignals")
                {
                    _signalProvider ??= new StandardConfluenceSignalProvider();
                    _signalProvider.MapSeries(prop.Name, SignalType.GenericBullish, SignalDirection.Bullish);
                }
                else if (prop.Name == "BearishSignals" || prop.Name == "SellSignals")
                {
                    _signalProvider ??= new StandardConfluenceSignalProvider();
                    _signalProvider.MapSeries(prop.Name, SignalType.GenericBearish, SignalDirection.Bearish);
                }
            }
        }

        return IndicatorResult.Success(series, signalProvider: _signalProvider);
    }
}
