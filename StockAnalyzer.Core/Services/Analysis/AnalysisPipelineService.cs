using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Default implementation of the Analysis Pipeline Service.
/// </summary>
public class AnalysisPipelineService : IAnalysisPipelineService
{
    private readonly IReverseWatchAnalysisService _reverseWatchService;
    private readonly IPythonService? _pythonService;
    private readonly IIndicatorFactory _indicatorFactory;

    public AnalysisPipelineService(IPythonService? pythonService = null, IIndicatorFactory? indicatorFactory = null)
    {
        _reverseWatchService = new ReverseWatchAnalysisService();
        _pythonService = pythonService;
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
    }

    /// <summary>
    /// Flexible constructor for DI if needed later.
    /// </summary>
    public AnalysisPipelineService(IPythonService pythonService, IReverseWatchAnalysisService reverseWatchService, IIndicatorFactory? indicatorFactory = null)
    {
        _pythonService = pythonService;
        _reverseWatchService = reverseWatchService;
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
    }

    public Dictionary<string, IIndicatorResult> CalculateIndicators(
        IReadOnlyList<CoreCandleData> candles, 
        IEnumerable<CoreIndicatorSettings> settings)
    {
        var result = new Dictionary<string, IIndicatorResult>();
        if (candles == null || settings == null) return result;

        if (candles.Count == 0) return result;

        foreach (var setting in settings)
        {
            // if (!setting.IsEnabled) continue; // Step 72: Always calculate to show values in sidebar
            if (!setting.TypeEnum.HasValue) continue;

            ICoreIndicator? indicator = null;
            try
            {
                // Create indicator via Factory (auto-registered indicators only)
                indicator = _indicatorFactory.Create(setting.TypeEnum.Value, setting.ParameterObject);
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"IndicatorFactory.Create Failed for {setting.TypeEnum}: {ex}");
                 result[setting.Id] = IndicatorResult.Failure($"Init Error: {ex.Message}");
                 continue;
            }

            if (indicator != null)
            {
                try
                {
                    IExecutionContext context = new CoreExecutionContext(_pythonService);

                    IIndicatorResult indicatorResult = System.Threading.Tasks.Task.Run(async () =>
                    {
                        return await indicator.CalculateAsync(candles, context).ConfigureAwait(false);
                    }).GetAwaiter().GetResult();

                    result[setting.Id] = indicatorResult;

                    if (!indicatorResult.IsSuccessful)
                    {
                        // Log error for debugging (future: propagate to UI)
                        System.Diagnostics.Debug.WriteLine($"Indicator {setting.DisplayName} failed: {indicatorResult.ErrorMessage}");
                        setting.ErrorMessage = indicatorResult.ErrorMessage;
                    }
                    else
                    {
                        setting.ErrorMessage = null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Indicator {setting.DisplayName} CRASHED: {ex}");
                    setting.ErrorMessage = $"Crash: {ex.Message}";
                    result[setting.Id] = IndicatorResult.Failure($"Crash: {ex.Message}");
                }
            }
            // Note: Unregistered indicator types (e.g., BB) are silently skipped.
            // These require special handling or future Factory migration.
        }

        return result;
    }

    public async System.Threading.Tasks.Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles, 
        IEnumerable<CoreIndicatorSettings> settings)
    {
        var result = new Dictionary<string, IIndicatorResult>();
        if (candles == null || settings == null) return result;

        if (candles.Count == 0) return result;

        var settingNames = string.Join(", ", settings.Select(s => s.DisplayName));

        foreach (var setting in settings)
        {
            // if (!setting.IsEnabled) continue; // Step 72: Always calculate to show values in sidebar
            if (!setting.TypeEnum.HasValue) continue;

            ICoreIndicator? indicator = null;
            try
            {
                indicator = _indicatorFactory.Create(setting.TypeEnum.Value, setting.ParameterObject);
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"IndicatorFactory.Create Failed for {setting.TypeEnum}: {ex}");
                 result[setting.Id] = IndicatorResult.Failure($"Init Error: {ex.Message}");
                 continue;
            }

            if (indicator != null)
            {
                try
                {
                    IExecutionContext context = new CoreExecutionContext(_pythonService);

                    IIndicatorResult indicatorResult = await indicator.CalculateAsync(candles, context).ConfigureAwait(false);

                    result[setting.Id] = indicatorResult;

                    if (!indicatorResult.IsSuccessful)
                    {
                        System.Diagnostics.Debug.WriteLine($"Indicator {setting.DisplayName} failed: {indicatorResult.ErrorMessage}");
                        setting.ErrorMessage = indicatorResult.ErrorMessage;
                    }
                    else
                    {
                        setting.ErrorMessage = null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Indicator {setting.DisplayName} CRASHED: {ex}");
                    setting.ErrorMessage = $"Crash: {ex.Message}";
                    result[setting.Id] = IndicatorResult.Failure($"Crash: {ex.Message}");
                }
            }
            else
            {
                // MESA or other indicators returning null are explicitly unmapped or failing early
            }
        }

        return result;
    }

    public ReverseWatchCurveData? CalculateReverseWatch(
        IReadOnlyList<CoreCandleData> candles, 
        int period, 
        string symbol,
        bool isMaBased = true,
        bool isLogScaleVolume = false)
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
            return _reverseWatchService.Calculate(candleList, period, symbol, isMaBased, isLogScaleVolume);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReverseWatch Calc Error: {ex.Message}");
            return null;
        }
    }

    public decimal CalculateAtr(IReadOnlyList<CoreCandleData> candles, int period)
    {
        if (candles == null) return 0m;
        
        // Use a lightweight enumerable that avoids allocation if possible in the calculator
        var candleList = candles.Select(c => new CandleData(
             c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));

        return AtrCalculator.Calculate(candleList, period);
    }
}
