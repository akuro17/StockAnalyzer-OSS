using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<AnalysisPipelineService> _logger;

    public AnalysisPipelineService(IPythonService? pythonService = null, IIndicatorFactory? indicatorFactory = null, ILogger<AnalysisPipelineService>? logger = null)
    {
        _reverseWatchService = new ReverseWatchAnalysisService();
        _pythonService = pythonService;
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
        _logger = logger ?? NullLogger<AnalysisPipelineService>.Instance;
    }

    /// <summary>
    /// Flexible constructor for DI if needed later.
    /// </summary>
    public AnalysisPipelineService(IPythonService pythonService, IReverseWatchAnalysisService reverseWatchService, IIndicatorFactory? indicatorFactory = null, ILogger<AnalysisPipelineService>? logger = null)
    {
        _pythonService = pythonService;
        _reverseWatchService = reverseWatchService;
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
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

    public Dictionary<string, IIndicatorResult> CalculateIndicators(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings)
    {
        var result = new Dictionary<string, IIndicatorResult>();
        if (candles == null || settings == null || candles.Count == 0) return result;

        var orderedSettings = SortSettingsByDependency(settings, _logger);
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

                    IIndicatorResult indicatorResult;

                    bool hasChaining = !string.IsNullOrEmpty(setting.SourceIndicatorId) || !string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId);

                    if (hasChaining)
                    {
                        IReadOnlyList<decimal?> inputSeries;
                        if (!string.IsNullOrEmpty(setting.SourceIndicatorId) &&
                            result.TryGetValue(setting.SourceIndicatorId, out var sourceResult) &&
                            sourceResult.IsSuccessful &&
                            sourceResult.MainValues.Count > 0)
                        {
                            inputSeries = sourceResult.MainValues;
                        }
                        else
                        {
                            inputSeries = PriceDataHelper.ExtractPriceSeries(candles, setting.PriceSource);
                        }

                        IReadOnlyList<decimal?>? dynamicPeriods = null;
                        if (!string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId) &&
                            result.TryGetValue(setting.DynamicPeriodIndicatorId, out var dynResult) &&
                            dynResult.IsSuccessful &&
                            dynResult.MainValues.Count > 0)
                        {
                            IReadOnlyList<decimal?> rawDriver = AdaptiveSmoothingHelper.SmoothDriverSeries(dynResult.MainValues);

                            IndicatorType? driverType = settingsById.TryGetValue(setting.DynamicPeriodIndicatorId, out var driverSetting)
                                ? driverSetting.TypeEnum
                                : null;

                            // Period-native drivers (Hilbert Transform, FFT Cycle) already output bar counts and are used as-is.
                            // Other drivers (e.g. a price-scale SMA/EMA) must be normalized into a period range first, otherwise
                            // their raw values saturate at the clamp bound and the "dynamic" period becomes effectively constant.
                            dynamicPeriods = AdaptiveSmoothingHelper.IsPeriodNativeDriverType(driverType)
                                ? rawDriver
                                : AdaptiveSmoothingHelper.NormalizeDriverSeries(rawDriver);
                        }

                        indicatorResult = indicator.CalculateSeries(inputSeries, dynamicPeriods);
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
        IEnumerable<CoreIndicatorSettings> settings)
    {
        var result = new Dictionary<string, IIndicatorResult>();
        if (candles == null || settings == null || candles.Count == 0) return result;

        var orderedSettings = SortSettingsByDependency(settings, _logger);
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

                    IIndicatorResult indicatorResult;

                    bool hasChaining = !string.IsNullOrEmpty(setting.SourceIndicatorId) || !string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId);

                    if (hasChaining)
                    {
                        IReadOnlyList<decimal?> inputSeries;
                        if (!string.IsNullOrEmpty(setting.SourceIndicatorId) &&
                            result.TryGetValue(setting.SourceIndicatorId, out var sourceResult) &&
                            sourceResult.IsSuccessful &&
                            sourceResult.MainValues.Count > 0)
                        {
                            inputSeries = sourceResult.MainValues;
                        }
                        else
                        {
                            inputSeries = PriceDataHelper.ExtractPriceSeries(candles, setting.PriceSource);
                        }

                        IReadOnlyList<decimal?>? dynamicPeriods = null;
                        if (!string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId) &&
                            result.TryGetValue(setting.DynamicPeriodIndicatorId, out var dynResult) &&
                            dynResult.IsSuccessful &&
                            dynResult.MainValues.Count > 0)
                        {
                            IReadOnlyList<decimal?> rawDriver = AdaptiveSmoothingHelper.SmoothDriverSeries(dynResult.MainValues);

                            IndicatorType? driverType = settingsById.TryGetValue(setting.DynamicPeriodIndicatorId, out var driverSetting)
                                ? driverSetting.TypeEnum
                                : null;

                            // Period-native drivers (Hilbert Transform, FFT Cycle) already output bar counts and are used as-is.
                            // Other drivers (e.g. a price-scale SMA/EMA) must be normalized into a period range first, otherwise
                            // their raw values saturate at the clamp bound and the "dynamic" period becomes effectively constant.
                            dynamicPeriods = AdaptiveSmoothingHelper.IsPeriodNativeDriverType(driverType)
                                ? rawDriver
                                : AdaptiveSmoothingHelper.NormalizeDriverSeries(rawDriver);
                        }

                        indicatorResult = indicator.CalculateSeries(inputSeries, dynamicPeriods);
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
}
