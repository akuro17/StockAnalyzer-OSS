using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>View state for the DI-backed phase-space spiral analysis panel.</summary>
public partial class SpiralAnalysisViewModel : ViewModelBase, IDisposable
{
    private const int AnalysisDebounceMilliseconds = 200;
    private readonly ISpiralAnalysisDataSource _dataSource;
    private readonly IDispatcherService _dispatcherService;
    private readonly object _analysisGate = new();
    private long? _lastAppliedDataRevision;
    private long _analysisRequestVersion;
    private CancellationTokenSource? _analysisCancellation;
    private bool _isApplyingDataDefaults;
    private bool _isApplyingDynamicInitialRadius;
    private bool _isUpdatingModelDefaults;
    private bool _disposed;

    [ObservableProperty] private uint? _barsPerTurn = 20;
    [ObservableProperty] private decimal? _initialRadius;
    [ObservableProperty] private bool _autoInitialRadius = true;
    [ObservableProperty] private decimal? _growthPerRadian = 0.1m;
    [ObservableProperty] private uint? _startIndex;
    [ObservableProperty] private uint? _endIndex;
    [ObservableProperty] private decimal? _gridPriceStep;
    [ObservableProperty] private uint? _gridOpacityPercent = 60;
    [ObservableProperty] private bool _gridDashed;
    [ObservableProperty] private bool _useVisibleRangeOrigin;
    [ObservableProperty] private SpiralPriceModelKind _selectedModel = SpiralPriceModelKind.Logarithmic;
    [ObservableProperty] private PriceType _selectedPriceType = PriceType.Typical;
    [ObservableProperty] private bool _isClockwise;
    [ObservableProperty] private bool _hasInvalidInput;
    [ObservableProperty] private SpiralAnalysisResult? _result;

    public static IReadOnlyList<SpiralPriceModelKind> ModelOptions { get; } =
        new[] { SpiralPriceModelKind.Logarithmic, SpiralPriceModelKind.Archimedean, SpiralPriceModelKind.Golden };

    public static IReadOnlyList<PriceType> PriceTypeOptions => PriceDataHelper.PriceTypeOptions;

    public bool IsGrowthInputEnabled => SelectedModel != SpiralPriceModelKind.Golden;
    public bool IsArchimedeanModel => SelectedModel == SpiralPriceModelKind.Archimedean;
    public bool IsLogarithmicModel => SelectedModel == SpiralPriceModelKind.Logarithmic;
    public bool IsGoldenModel => SelectedModel == SpiralPriceModelKind.Golden;

    public SpiralAnalysisViewModel(ISpiralAnalysisDataSource dataSource, IDispatcherService dispatcherService)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _dataSource.Changed += OnDataSourceChanged;
        UpdateFromDataSource();
    }

    private void OnDataSourceChanged(object? sender, EventArgs eventArgs)
    {
        if (_dispatcherService.CheckAccess())
        {
            UpdateFromDataSource();
            return;
        }

        _dispatcherService.Post(static viewModel => viewModel.UpdateFromDataSource(), this);
    }

    private void UpdateFromDataSource()
    {
        bool dataRevisionChanged = false;
        if (_dataSource is ISpiralAnalysisCandleRangeSource source)
        {
            if (_dataSource is ISpiralAnalysisDataRevisionSource revisionSource)
            {
                if (_lastAppliedDataRevision != revisionSource.DataRevision)
                {
                    _lastAppliedDataRevision = revisionSource.DataRevision;
                    ApplyDataDefaults(source);
                    dataRevisionChanged = true;
                }
            }
            else if (!StartIndex.HasValue && !EndIndex.HasValue)
            {
                ApplyDataDefaults(source);
                dataRevisionChanged = true;
            }
        }

        Result = _dataSource.Current;
        if (dataRevisionChanged)
        {
            RequestAnalysis();
        }
    }

    private void ApplyDataDefaults(ISpiralAnalysisCandleRangeSource source)
    {
        _isApplyingDataDefaults = true;
        try
        {
            AutoInitialRadius = true;
            StartIndex = source.CandleCount >= 2 ? 0u : null;
            EndIndex = source.CandleCount >= 2 ? (uint)source.CandleCount - 1 : null;
        }
        finally
        {
            _isApplyingDataDefaults = false;
        }

        TryApplyDynamicInitialRadius();
    }

    private bool TryApplyDynamicInitialRadius()
    {
        if (!AutoInitialRadius || !TryGetRange(out uint startIndex, out uint endIndex) ||
            !TryGetFirstPositiveAppliedPrice(startIndex, endIndex, out decimal price))
        {
            if (AutoInitialRadius)
            {
                SetDynamicInitialRadius(null);
            }

            return false;
        }

        SetDynamicInitialRadius(price);
        return true;
    }

    private bool TryGetFirstPositiveAppliedPrice(uint startIndex, uint endIndex, out decimal price)
    {
        if (_dataSource is ISpiralAnalysisPriceRangeSource priceSource)
        {
            return priceSource.TryGetFirstPositivePrice(startIndex, endIndex, SelectedPriceType, out price);
        }

        if (SelectedPriceType == PriceType.Close && _dataSource is ISpiralAnalysisCandleRangeSource closeSource)
        {
            return closeSource.TryGetFirstPositiveClose(startIndex, endIndex, out price);
        }

        price = 0m;
        return false;
    }

    private void SetDynamicInitialRadius(decimal? value)
    {
        _isApplyingDynamicInitialRadius = true;
        try
        {
            InitialRadius = value;
        }
        finally
        {
            _isApplyingDynamicInitialRadius = false;
        }
    }

    private void RequestAnalysis()
    {
        if (_disposed || _isApplyingDataDefaults)
        {
            return;
        }

        long requestVersion;
        lock (_analysisGate)
        {
            requestVersion = ++_analysisRequestVersion;
            _analysisCancellation?.Cancel();
            _analysisCancellation = null;
        }

        if (!TryCreateParameters(out SpiralAnalysisParameters parameters))
        {
            Result = null;
            HasInvalidInput = true;
            return;
        }
        HasInvalidInput = false;

        var cancellation = new CancellationTokenSource();
        lock (_analysisGate)
        {
            if (_disposed || requestVersion != _analysisRequestVersion)
            {
                cancellation.Dispose();
                return;
            }

            _analysisCancellation = cancellation;
        }
        _ = RunAnalysisAsync(requestVersion, parameters, cancellation);
    }

    private bool TryCreateParameters(out SpiralAnalysisParameters parameters)
    {
        parameters = default;
        if (!_dataSource.HasCandles || !InitialRadius.HasValue || !BarsPerTurn.HasValue ||
            !TryGetRange(out uint startIndex, out uint endIndex))
        {
            return false;
        }

        if (SelectedModel != SpiralPriceModelKind.Golden && !GrowthPerRadian.HasValue)
        {
            return false;
        }

        double? radialGrowth = SelectedModel == SpiralPriceModelKind.Archimedean
            ? (double)GrowthPerRadian!.Value
            : null;
        double? logarithmicGrowth = SelectedModel == SpiralPriceModelKind.Logarithmic
            ? (double)GrowthPerRadian!.Value
            : null;
        parameters = new SpiralAnalysisParameters(BarsPerTurn.Value, startIndex, SelectedModel, InitialRadius.Value,
            radialGrowth, logarithmicGrowth, endIndex, SelectedPriceType, IsClockwise);
        return true;
    }

    private async Task RunAnalysisAsync(long requestVersion, SpiralAnalysisParameters parameters, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(AnalysisDebounceMilliseconds, cancellation.Token).ConfigureAwait(false);
            SpiralAnalysisResult result;
            if (_dataSource is ISpiralAnalysisComputationSource computationSource)
            {
                result = await computationSource.ComputeAsync(parameters, cancellation.Token).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => _dataSource.Analyze(parameters), cancellation.Token).ConfigureAwait(false);
                result = _dataSource.Current ?? throw new InvalidOperationException("The analysis data source returned no result.");
            }

            if (cancellation.IsCancellationRequested || requestVersion != _analysisRequestVersion)
            {
                return;
            }

            await _dispatcherService.PostAsync(static state =>
            {
                if (!state.Cancellation.IsCancellationRequested && state.Version == state.ViewModel._analysisRequestVersion)
                    state.ViewModel.Result = state.Result;
                return Task.CompletedTask;
            }, (ViewModel: this, Result: result, Version: requestVersion, Cancellation: cancellation));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentException)
        {
            if (!cancellation.IsCancellationRequested && requestVersion == _analysisRequestVersion)
            {
                await _dispatcherService.PostAsync(static viewModel =>
                {
                    viewModel.Result = null;
                    viewModel.HasInvalidInput = true;
                    return Task.CompletedTask;
                }, this);
            }
        }
        finally
        {
            lock (_analysisGate)
            {
                if (ReferenceEquals(_analysisCancellation, cancellation))
                {
                    _analysisCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    partial void OnBarsPerTurnChanged(uint? value) => RequestAnalysis();

    partial void OnInitialRadiusChanged(decimal? value)
    {
        if (!_isApplyingDynamicInitialRadius)
        {
            AutoInitialRadius = false;
        }

        RequestAnalysis();
    }

    partial void OnAutoInitialRadiusChanged(bool value)
    {
        if (value && !_isApplyingDataDefaults)
        {
            TryApplyDynamicInitialRadius();
        }

        RequestAnalysis();
    }

    partial void OnGrowthPerRadianChanged(decimal? value)
    {
        if (!_isUpdatingModelDefaults)
        {
            RequestAnalysis();
        }
    }

    partial void OnSelectedModelChanged(SpiralPriceModelKind value)
    {
        _isUpdatingModelDefaults = true;
        try
        {
            GrowthPerRadian = value switch
            {
                SpiralPriceModelKind.Archimedean => 2m,
                SpiralPriceModelKind.Logarithmic => 0.1m,
                _ => null
            };
        }
        finally
        {
            _isUpdatingModelDefaults = false;
        }

        OnPropertyChanged(nameof(IsGrowthInputEnabled));
        OnPropertyChanged(nameof(IsArchimedeanModel));
        OnPropertyChanged(nameof(IsLogarithmicModel));
        OnPropertyChanged(nameof(IsGoldenModel));
        RequestAnalysis();
    }

    partial void OnSelectedPriceTypeChanged(PriceType value)
    {
        if (AutoInitialRadius)
        {
            TryApplyDynamicInitialRadius();
        }

        RequestAnalysis();
    }

    partial void OnIsClockwiseChanged(bool value) => RequestAnalysis();

    partial void OnStartIndexChanged(uint? value) => OnRangeChanged();
    partial void OnEndIndexChanged(uint? value) => OnRangeChanged();

    private void OnRangeChanged()
    {
        if (!_isApplyingDataDefaults && AutoInitialRadius)
        {
            TryApplyDynamicInitialRadius();
        }

        RequestAnalysis();
    }

    private bool TryGetRange(out uint startIndex, out uint endIndex)
    {
        startIndex = 0;
        endIndex = 0;
        if (!_dataSource.HasCandles || !StartIndex.HasValue || !EndIndex.HasValue ||
            StartIndex.Value >= EndIndex.Value || _dataSource is not ISpiralAnalysisCandleRangeSource source ||
            EndIndex.Value >= (uint)source.CandleCount)
        {
            return false;
        }

        startIndex = StartIndex.Value;
        endIndex = EndIndex.Value;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_analysisGate)
        {
            _disposed = true;
            _analysisRequestVersion++;
            _analysisCancellation?.Cancel();
            _analysisCancellation = null;
        }
        _dataSource.Changed -= OnDataSourceChanged;
    }
}
