using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Default implementation of the Analysis Pipeline Service with DAG dependency resolution
/// for Indicator-on-Indicator chaining and dynamic adaptive modulation.
/// </summary>
public class AnalysisPipelineService : IAnalysisPipelineService
{
    private readonly IReverseWatchAnalysisService _reverseWatchService;
    private readonly IPythonService? _pythonService;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly ISourceIndicatorService? _sourceIndicatorService;
    private readonly IDynamicPeriodDriverService? _dynamicPeriodDriverService;
    private readonly ILogger<AnalysisPipelineService> _logger;

    public AnalysisPipelineService(
        IPythonService? pythonService = null, 
        IIndicatorFactory? indicatorFactory = null, 
        ISourceIndicatorService? sourceIndicatorService = null,
        ILogger<AnalysisPipelineService>? logger = null,
        IDynamicPeriodDriverService? dynamicPeriodDriverService = null)
    {
        _reverseWatchService = new ReverseWatchAnalysisService();
        _pythonService = pythonService;
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
        _sourceIndicatorService = sourceIndicatorService;
        _dynamicPeriodDriverService = dynamicPeriodDriverService;
        _logger = logger ?? NullLogger<AnalysisPipelineService>.Instance;
    }

    /// <summary>
    /// Flexible constructor for DI if needed later.
    /// </summary>
    public AnalysisPipelineService(
        IPythonService pythonService, 
        IReverseWatchAnalysisService reverseWatchService, 
        IIndicatorFactory? indicatorFactory = null, 
        ISourceIndicatorService? sourceIndicatorService = null,
        ILogger<AnalysisPipelineService>? logger = null,
        IDynamicPeriodDriverService? dynamicPeriodDriverService = null)
    {
        _pythonService = pythonService;
        _reverseWatchService = reverseWatchService;
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
        _sourceIndicatorService = sourceIndicatorService;
        _dynamicPeriodDriverService = dynamicPeriodDriverService;
        _logger = logger ?? NullLogger<AnalysisPipelineService>.Instance;
    }

    /// <summary>
    /// Topologically sorts indicator settings according to their SourceIndicatorId and DynamicPeriodIndicatorId dependencies.
    /// Safely handles circular dependencies without infinite loops or dropping items.
    /// </summary>
    public static List<CoreIndicatorSettings> SortSettingsByDependency(
        IEnumerable<CoreIndicatorSettings> settings, 
        ILogger? logger = null)
    {
        var settingsList = settings.ToList();
        if (settingsList.Count <= 1) return settingsList;

        var settingsMap = new Dictionary<string, CoreIndicatorSettings>();
        foreach (var s in settingsList)
        {
            if (!string.IsNullOrEmpty(s.Id))
            {
                settingsMap[s.Id] = s;
            }
        }

        var adj = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var s in settingsList)
        {
            adj[s.Id] = new List<string>();
            inDegree[s.Id] = 0;
        }

        foreach (var s in settingsList)
        {
            var deps = new HashSet<string>();
            if (!string.IsNullOrEmpty(s.SourceIndicatorId) && settingsMap.ContainsKey(s.SourceIndicatorId) && s.SourceIndicatorId != s.Id)
            {
                deps.Add(s.SourceIndicatorId);
            }
            if (!string.IsNullOrEmpty(s.DynamicPeriodIndicatorId) && settingsMap.ContainsKey(s.DynamicPeriodIndicatorId) && s.DynamicPeriodIndicatorId != s.Id)
            {
                deps.Add(s.DynamicPeriodIndicatorId);
            }

            foreach (var depId in deps)
            {
                adj[depId].Add(s.Id);
                inDegree[s.Id]++;
            }
        }

        var queue = new Queue<string>();
        foreach (var kvp in inDegree)
        {
            if (kvp.Value == 0)
            {
                queue.Enqueue(kvp.Key);
            }
        }

        var sorted = new List<CoreIndicatorSettings>();
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (settingsMap.TryGetValue(currentId, out var setting))
            {
                sorted.Add(setting);
            }

            foreach (var neighbor in adj[currentId])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        // Circular dependency fallback: Append any unprocessed settings
        if (sorted.Count < settingsList.Count)
        {
            logger?.LogWarning("[AnalysisPipelineService] Circular dependency detected in indicator settings. Appending remaining indicators.");
            foreach (var s in settingsList)
            {
                if (!sorted.Contains(s))
                {
                    sorted.Add(s);
                }
            }
        }

        return sorted;
    }

    private List<CoreIndicatorSettings> ResolveAllSettings(IEnumerable<CoreIndicatorSettings> settings)
    {
        var list = settings?.ToList() ?? new List<CoreIndicatorSettings>();
        if ((_sourceIndicatorService == null && _dynamicPeriodDriverService == null) || list.Count == 0)
        {
            return list;
        }

        var expanded = new List<CoreIndicatorSettings>(list);
        var visitedIds = new HashSet<string>(list.Where(s => !string.IsNullOrEmpty(s.Id)).Select(s => s.Id!));
        var queue = new Queue<CoreIndicatorSettings>(list);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            CheckAndEnqueue(current.SourceIndicatorId, isSource: true);
            CheckAndEnqueue(current.DynamicPeriodIndicatorId, isSource: false);
        }

        void CheckAndEnqueue(string? refId, bool isSource)
        {
            if (!string.IsNullOrEmpty(refId) && visitedIds.Add(refId))
            {
                CoreIndicatorSettings? setting = null;
                if (isSource)
                {
                    setting = _sourceIndicatorService?.GetSourceIndicator(refId)
                        ?? _dynamicPeriodDriverService?.GetDynamicPeriodDriver(refId);
                }
                else
                {
                    setting = _dynamicPeriodDriverService?.GetDynamicPeriodDriver(refId)
                        ?? _sourceIndicatorService?.GetSourceIndicator(refId);
                }

                if (setting != null)
                {
                    expanded.Add(setting);
                    queue.Enqueue(setting);
                }
            }
        }

        return expanded;
    }

    /// <summary>
    /// Resolves the input series and (optional) dynamic periods for a chained indicator (one whose
    /// SourceIndicatorId and/or DynamicPeriodIndicatorId is set), falling back to the raw Price series
    /// / static Period when a referenced indicator cannot be resolved or produced no data. Any fallback
    /// is surfaced via the returned ChainWarnings so the caller can populate setting.ErrorMessage.
    /// </summary>
    private (IReadOnlyList<decimal?> InputSeries, IReadOnlyList<decimal?>? DynamicPeriods, List<string>? ChainWarnings) ResolveChainedInputs(
        CoreIndicatorSettings setting,
        Dictionary<string, IIndicatorResult> result,
        Dictionary<string, CoreIndicatorSettings> settingsById,
        IReadOnlyList<CoreCandleData> candles)
    {
        List<string>? chainWarnings = null;

        IReadOnlyList<decimal?> inputSeries;
        if (!string.IsNullOrEmpty(setting.SourceIndicatorId))
        {
            if (result.TryGetValue(setting.SourceIndicatorId, out var sourceResult) && sourceResult.IsSuccessful)
            {
                string? targetSeriesName = settingsById.TryGetValue(setting.SourceIndicatorId, out var srcSetting)
                    ? srcSetting.OutputSeriesName
                    : null;

                IReadOnlyList<decimal?> series = !string.IsNullOrEmpty(targetSeriesName)
                    ? sourceResult.GetSeries(targetSeriesName)
                    : sourceResult.MainValues;

                if (series.Count == 0 && !string.IsNullOrEmpty(targetSeriesName) && targetSeriesName != IndicatorResult.MainSeriesName)
                {
                    series = sourceResult.MainValues;
                }

                if (series.Count > 0)
                {
                    inputSeries = series;
                }
                else
                {
                    inputSeries = PriceDataHelper.ExtractPriceSeries(candles, setting.PriceSource);
                    (chainWarnings ??= new List<string>()).Add("Base Indicator reference produced no data; using Price Source instead.");
                }
            }
            else
            {
                inputSeries = PriceDataHelper.ExtractPriceSeries(candles, setting.PriceSource);
                (chainWarnings ??= new List<string>()).Add("Base Indicator reference not found (it may have been deleted); using Price Source instead.");
            }
        }
        else
        {
            inputSeries = PriceDataHelper.ExtractPriceSeries(candles, setting.PriceSource);
        }

        IReadOnlyList<decimal?>? dynamicPeriods = null;
        if (!string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId))
        {
            if (result.TryGetValue(setting.DynamicPeriodIndicatorId, out var dynResult) && dynResult.IsSuccessful)
            {
                string? dynSeriesName = settingsById.TryGetValue(setting.DynamicPeriodIndicatorId, out var dynSetting)
                    ? dynSetting.OutputSeriesName
                    : null;

                IReadOnlyList<decimal?> driverVals = !string.IsNullOrEmpty(dynSeriesName)
                    ? dynResult.GetSeries(dynSeriesName)
                    : dynResult.MainValues;

                if (driverVals.Count == 0 && !string.IsNullOrEmpty(dynSeriesName) && dynSeriesName != IndicatorResult.MainSeriesName)
                {
                    driverVals = dynResult.MainValues;
                }

                if (driverVals.Count > 0)
                {
                    IndicatorType? driverType = settingsById.TryGetValue(setting.DynamicPeriodIndicatorId, out var driverSetting)
                        ? driverSetting.TypeEnum
                        : null;

                    // Causality guard: Ichimoku ChikouSpan contains future price data (Close of t+25 placed at t).
                    // Using it directly as a dynamic period driver introduces lookahead bias and leaves recent 25 bars uncalculated (null).
                    // Fallback safely to current bar Close price.
                    if (driverType == IndicatorType.Ichimoku && string.Equals(dynSeriesName, "Chikou", StringComparison.OrdinalIgnoreCase))
                    {
                        driverVals = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Close);
                    }

                    IReadOnlyList<decimal?> rawDriver = AdaptiveSmoothingHelper.SmoothDriverSeries(driverVals);

                    // Period-native drivers (Hilbert Transform, FFT Cycle) already output bar counts and are used as-is.
                    // Other drivers (e.g. a price-scale SMA/EMA) must be normalized into a period range first, otherwise
                    // their raw values saturate at the clamp bound and the "dynamic" period becomes effectively constant.
                    dynamicPeriods = AdaptiveSmoothingHelper.IsPeriodNativeDriverType(driverType)
                        ? rawDriver
                        : AdaptiveSmoothingHelper.NormalizeDriverSeries(rawDriver);
                }
                else
                {
                    (chainWarnings ??= new List<string>()).Add("Adaptive Period reference produced no data; using static Period instead.");
                }
            }
            else
            {
                (chainWarnings ??= new List<string>()).Add("Adaptive Period reference not found (it may have been deleted); using static Period instead.");
            }
        }

        return (inputSeries, dynamicPeriods, chainWarnings);
    }

    public Dictionary<string, IIndicatorResult> CalculateIndicators(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)

    {
        var result = new Dictionary<string, IIndicatorResult>();
        if (candles == null || settings == null || candles.Count == 0) return result;

        var allSettings = ResolveAllSettings(settings);
        var orderedSettings = SortSettingsByDependency(allSettings, _logger);
        var settingsById = new Dictionary<string, CoreIndicatorSettings>();
        foreach (var s in orderedSettings)
        {
            settingsById[s.Id] = s;
        }

        foreach (var setting in orderedSettings)
        {
            if (!setting.TypeEnum.HasValue) continue;

            ICoreIndicator? indicator = null;
            try
            {
                indicator = _indicatorFactory.Create(setting.TypeEnum.Value, setting.ParameterObject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AnalysisPipelineService] IndicatorFactory.Create Failed for {IndicatorType}", setting.TypeEnum);
                result[setting.Id] = IndicatorResult.Failure($"Init Error: {ex.Message}");
                continue;
            }

            if (indicator != null)
            {
                try
                {
                    if (indicator is CoreIndicatorBase baseInd)
                    {
                        baseInd.PriceSource = setting.PriceSource;
                    }

                    if (indicator is Core.Models.Indicators.Statistics.CoreCorrelationIndicator corrInd && !string.IsNullOrWhiteSpace(corrInd.ComparisonSymbol))
                    {
                        var secCandles = ResolveSecondaryCandles(secondaryCandlesBySymbol, corrInd.ComparisonSymbol);
                        corrInd.SetSecondaryCandles(secCandles);
                    }



                    IIndicatorResult indicatorResult;


                    bool hasChaining = !string.IsNullOrEmpty(setting.SourceIndicatorId) || !string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId);
                    List<string>? chainWarnings = null;

                    if (hasChaining)
                    {
                        var chained = ResolveChainedInputs(setting, result, settingsById, candles);
                        chainWarnings = chained.ChainWarnings;
                        indicatorResult = indicator.CalculateSeries(chained.InputSeries, chained.DynamicPeriods);
                    }
                    else
                    {
                        IExecutionContext context = new CoreExecutionContext(_pythonService);
                        indicatorResult = Task.Run(async () =>
                        {
                            return await indicator.CalculateAsync(candles, context).ConfigureAwait(false);
                        }).GetAwaiter().GetResult();
                    }

                    result[setting.Id] = indicatorResult;

                    if (!indicatorResult.IsSuccessful)
                    {
                        _logger.LogWarning("[AnalysisPipelineService] Indicator {DisplayName} failed: {ErrorMessage}", setting.DisplayName, indicatorResult.ErrorMessage);
                        setting.ErrorMessage = indicatorResult.ErrorMessage;
                    }
                    else if (chainWarnings != null)
                    {
                        string warningText = string.Join(" ", chainWarnings);
                        _logger.LogWarning("[AnalysisPipelineService] Indicator {DisplayName} using fallback input: {Warning}", setting.DisplayName, warningText);
                        setting.ErrorMessage = warningText;
                    }
                    else
                    {
                        setting.ErrorMessage = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AnalysisPipelineService] Indicator {DisplayName} CRASHED", setting.DisplayName);
                    setting.ErrorMessage = $"Crash: {ex.Message}";
                    result[setting.Id] = IndicatorResult.Failure($"Crash: {ex.Message}");
                }
            }
        }

        return result;
    }

    public async Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)

    {
        var result = new Dictionary<string, IIndicatorResult>();
        if (candles == null || settings == null || candles.Count == 0) return result;

        var allSettings = ResolveAllSettings(settings);
        var orderedSettings = SortSettingsByDependency(allSettings, _logger);
        var settingsById = new Dictionary<string, CoreIndicatorSettings>();
        foreach (var s in orderedSettings)
        {
            settingsById[s.Id] = s;
        }

        foreach (var setting in orderedSettings)
        {
            if (!setting.TypeEnum.HasValue) continue;

            ICoreIndicator? indicator = null;
            try
            {
                indicator = _indicatorFactory.Create(setting.TypeEnum.Value, setting.ParameterObject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AnalysisPipelineService] IndicatorFactory.Create Failed for {IndicatorType}", setting.TypeEnum);
                result[setting.Id] = IndicatorResult.Failure($"Init Error: {ex.Message}");
                continue;
            }

            if (indicator != null)
            {
                try
                {
                    if (indicator is CoreIndicatorBase baseInd)
                    {
                        baseInd.PriceSource = setting.PriceSource;
                    }

                    if (indicator is Core.Models.Indicators.Statistics.CoreCorrelationIndicator corrInd && !string.IsNullOrWhiteSpace(corrInd.ComparisonSymbol))
                    {
                        var secCandles = ResolveSecondaryCandles(secondaryCandlesBySymbol, corrInd.ComparisonSymbol);
                        corrInd.SetSecondaryCandles(secCandles);
                    }



                    IIndicatorResult indicatorResult;


                    bool hasChaining = !string.IsNullOrEmpty(setting.SourceIndicatorId) || !string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId);
                    List<string>? chainWarnings = null;

                    if (hasChaining)
                    {
                        var chained = ResolveChainedInputs(setting, result, settingsById, candles);
                        chainWarnings = chained.ChainWarnings;
                        indicatorResult = indicator.CalculateSeries(chained.InputSeries, chained.DynamicPeriods);
                    }
                    else
                    {
                        IExecutionContext context = new CoreExecutionContext(_pythonService);
                        indicatorResult = await indicator.CalculateAsync(candles, context).ConfigureAwait(false);
                    }

                    result[setting.Id] = indicatorResult;

                    if (!indicatorResult.IsSuccessful)
                    {
                        _logger.LogWarning("[AnalysisPipelineService] Indicator {DisplayName} failed: {ErrorMessage}", setting.DisplayName, indicatorResult.ErrorMessage);
                        setting.ErrorMessage = indicatorResult.ErrorMessage;
                    }
                    else if (chainWarnings != null)
                    {
                        string warningText = string.Join(" ", chainWarnings);
                        _logger.LogWarning("[AnalysisPipelineService] Indicator {DisplayName} using fallback input: {Warning}", setting.DisplayName, warningText);
                        setting.ErrorMessage = warningText;
                    }
                    else
                    {
                        setting.ErrorMessage = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AnalysisPipelineService] Indicator {DisplayName} CRASHED", setting.DisplayName);
                    setting.ErrorMessage = $"Crash: {ex.Message}";
                    result[setting.Id] = IndicatorResult.Failure($"Crash: {ex.Message}");
                }
            }
        }

        return result;
    }

    public ReverseWatchCurveData? CalculateReverseWatch(
        IReadOnlyList<CoreCandleData> candles, 
        int period, 
        string symbol,
        bool isMaBased = true,
        bool isLogScaleVolume = false,
        int dataCount = 0)
    {
        if (candles == null) return null;

        var candleList = new List<CandleData>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
             var c = candles[i];
             candleList.Add(new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        }

        if (candleList.Count == 0) return null;

        try
        {
            return _reverseWatchService.Calculate(candleList, period, symbol, isMaBased, isLogScaleVolume, dataCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AnalysisPipelineService] ReverseWatch Calc Error");
            return null;
        }
    }

    public decimal CalculateAtr(IReadOnlyList<CoreCandleData> candles, int period)
    {
        if (candles == null) return 0m;
        
        var candleList = candles.Select(c => new CandleData(
             c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));

        return AtrCalculator.Calculate(candleList, period);
    }

    private static IReadOnlyList<CoreCandleData?>? ResolveSecondaryCandles(IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol, string symbol)
    {
        if (secondaryCandlesBySymbol == null || string.IsNullOrWhiteSpace(symbol)) return null;

        var key = symbol.Trim();
        if (secondaryCandlesBySymbol.TryGetValue(key, out var secCandles))
        {
            return secCandles;
        }

        var normKey = key.TrimStart('^').Replace('.', '-');
        foreach (var kvp in secondaryCandlesBySymbol)
        {
            if (string.Equals(kvp.Key.TrimStart('^').Replace('.', '-'), normKey, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }
}

