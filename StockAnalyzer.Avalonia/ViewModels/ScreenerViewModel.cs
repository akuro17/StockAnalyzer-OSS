using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ScreenerViewModel : ViewModelBase, IDisposable
{
    private readonly ScreenerService? _screenerService;
    private readonly PatternRecognitionService? _patternService;
    private readonly IStockAnalyzerSettings? _settings;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _statusResetCts;
    private bool _isDisposed;

    [ObservableProperty]
    private ObservableCollection<string> _targetSymbols = new();

    [ObservableProperty]
    private ObservableCollection<IScreeningCondition> _availableConditions = new();

    [ObservableProperty]
    private IScreeningCondition? _selectedCondition;

    [ObservableProperty]
    private ObservableCollection<string> _results = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotScanning))]
    private bool _isScanning;

    public bool IsNotScanning => !IsScanning;

    [ObservableProperty]
    private string _statusMessage = "Ready to scan.";

    public ScreenerViewModel(ScreenerService screenerService, PatternRecognitionService patternService, IStockAnalyzerSettings settings)
    {
        _screenerService = screenerService;
        _patternService = patternService;
        _settings = settings;
        Initialize();
    }

    // Default constructor for design-time
    public ScreenerViewModel()
    {
        // Mock service or null check handling might be needed if used at runtime without DI
        // But strictly for design time, properties can be initialized with dummy data
        InitializeFallback();
        // _screenerService is null here, catch usage if called
    }

    private void Initialize()
    {
        // Load default symbols from settings
        if (_settings?.DefaultScreenerSymbols != null)
        {
            foreach (var symbol in _settings.DefaultScreenerSymbols)
            {
                TargetSymbols.Add(symbol);
            }
        }

        // Register Conditions
        AvailableConditions.Add(new RsiOversoldCondition(ChartConstants.DefaultRsiPeriod, ChartConstants.DefaultRsiOversoldThreshold));
        
        // Register Divergence & Cross Conditions (RSI based by default for screening)
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBullishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBearishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.GoldenCross));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.DeadCross));
        
        if (_patternService != null)
        {
            // ML-based pattern matching (HeadAndShoulders, DoubleBottom, etc.)
            AvailableConditions.Add(new PatternMatchCondition(_patternService)); // Any pattern
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "HeadAndShoulders"));
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "InverseHeadAndShoulders"));
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "DoubleTop"));
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "DoubleBottom"));
        }

        // Register Geometric formations
        decimal geometricThreshold = _settings?.ZigzagThresholdPercent ?? ChartConstants.DefaultGeometricZigZagThreshold;
        AvailableConditions.Add(new GeometricPatternCondition(GeometricFormationType.BullishFlag, geometricThreshold));
        AvailableConditions.Add(new GeometricPatternCondition(GeometricFormationType.BearishFlag, geometricThreshold));

        // Register all classical candle patterns
        foreach (CandlePatternType patternType in Enum.GetValues(typeof(CandlePatternType)))
        {
            if (patternType != CandlePatternType.None)
            {
                AvailableConditions.Add(new CandlePatternCondition(patternType));
            }
        }

        // Register Market Structure conditions
        foreach (MarketStructureType msType in Enum.GetValues(typeof(MarketStructureType)))
        {
             // Typically we want a threshold > 0, using the default from settings or 5.0m as fallback
             decimal zigzagThreshold = _settings?.ZigzagThresholdPercent ?? 5.0m;
             AvailableConditions.Add(new MarketStructureCondition(msType, zigzagThreshold));
        }

        // Register Harmonic Pattern conditions
        foreach (StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType hpType in Enum.GetValues(typeof(StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType)))
        {
             AvailableConditions.Add(new HarmonicPatternCondition(hpType));
        }

        // Register Granville's Laws conditions
        foreach (GranvilleLawConditionType glType in Enum.GetValues(typeof(GranvilleLawConditionType)))
        {
             AvailableConditions.Add(new GranvilleLawCondition(glType));
        }

        // Register Elliott Wave conditions
        foreach (ElliottWaveConditionType ewType in Enum.GetValues(typeof(ElliottWaveConditionType)))
        {
             AvailableConditions.Add(new ElliottWaveCondition(ewType));
        }

        SelectedCondition = AvailableConditions.FirstOrDefault();
    }

    private void InitializeFallback()
    {
        TargetSymbols.Add("DesignData1");
        TargetSymbols.Add("DesignData2");
        AvailableConditions.Add(new RsiOversoldCondition(ChartConstants.DefaultRsiPeriod, ChartConstants.DefaultRsiOversoldThreshold));
        
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBullishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBearishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.GoldenCross));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.DeadCross));
        
        foreach (CandlePatternType patternType in Enum.GetValues(typeof(CandlePatternType)))
        {
            if (patternType != CandlePatternType.None)
            {
                AvailableConditions.Add(new CandlePatternCondition(patternType));
            }
        }

        foreach (MarketStructureType msType in Enum.GetValues(typeof(MarketStructureType)))
        {
             AvailableConditions.Add(new MarketStructureCondition(msType, 5.0m));
        }

        foreach (StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType hpType in Enum.GetValues(typeof(StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType)))
        {
             AvailableConditions.Add(new HarmonicPatternCondition(hpType));
        }

        foreach (GranvilleLawConditionType glType in Enum.GetValues(typeof(GranvilleLawConditionType)))
        {
             AvailableConditions.Add(new GranvilleLawCondition(glType));
        }

        // Register Elliott Wave conditions (fallback)
        foreach (ElliottWaveConditionType ewType in Enum.GetValues(typeof(ElliottWaveConditionType)))
        {
              AvailableConditions.Add(new ElliottWaveCondition(ewType));
        }

        SelectedCondition = AvailableConditions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_isDisposed || IsScanning) return;
        if (SelectedCondition == null)
        {
            StatusMessage = "Please select a screening condition.";
            _ = ScheduleStatusResetAsync();
            return;
        }

        if (_screenerService == null)
        {
             StatusMessage = "Screener service not initialized.";
             _ = ScheduleStatusResetAsync();
             return;
        }

        _statusResetCts?.Cancel();
        IsScanning = true;
        StatusMessage = "Scanning...";
        Results.Clear();
        _cts = new CancellationTokenSource();

        void UpdateProgress(int percent)
        {
            StatusMessage = $"Scanning... {percent}%";
        }

        var progress = new Progress<int>(UpdateProgress);

        try
        {

            // Use the service
            // Note: ScreenerService.ScreenAsync now takes IScreeningCondition
            var hits = await _screenerService.ScreenAsync(
                TargetSymbols.ToList(),
                SelectedCondition,
                TimeFrame.D1,
                progress,
                _cts.Token
            );

            foreach (var symbol in hits)
            {
                Results.Add(symbol);
            }

            StatusMessage = hits.Count > 0 
                ? $"Scan complete. Found {hits.Count} matches." 
                : "Scan complete. No matches found.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
            _ = ScheduleStatusResetAsync();
        }
    }

    private async Task ScheduleStatusResetAsync()
    {
        _statusResetCts?.Cancel();
        _statusResetCts = new CancellationTokenSource();
        var token = _statusResetCts.Token;

        try
        {
            await Task.Delay(5000, token);
            if (!token.IsCancellationRequested && !IsScanning)
            {
                StatusMessage = "Ready to scan.";
            }
        }
        catch (TaskCanceledException)
        {
            // Ignored, cancelled by another scan starting or component disposing
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        if (_isDisposed) return;
        _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _statusResetCts?.Cancel();
        _statusResetCts?.Dispose();
        _statusResetCts = null;

        GC.SuppressFinalize(this);
    }
}
