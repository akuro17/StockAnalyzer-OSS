using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Configuration;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Settings > Backtest: the user-defined default values of the Backtest window's Configuration groups
/// B (capital and cost model), C (margin model) and D (report aggregation). Group A is not covered.
/// Validation is delegated to <see cref="BacktestDefaultSettings.SnapshotValidated"/> (which reuses the
/// domain validators), so no bound is restated here. An invalid page is never persisted; the reason for each
/// rejected value is exposed through the per-item <c>*Error</c> properties (keyed by <see cref="BacktestDefaultSettings"/>
/// property name) so the view shows it inside that item as soon as the value is edited.
/// <see cref="ValidationMessage"/> only carries page-level save failures (e.g. I/O).
/// </summary>
public partial class BacktestSettingsViewModel : ViewModelBase, ISettingsPageViewModel
{
    private readonly IBacktestDefaultSettingsManager _manager;
    private BacktestDefaultSettings _snapshot = BacktestDefaultSettings.BuiltIn;
    private bool _isApplying;
    private bool _isRaisingDerived;

    public string TitleKey => "Settings_Backtest";
    public string IconKey => "SettingsBacktestIcon";

    public IReadOnlyList<PositionSizingModel> SizingModelOptions { get; } = Enum.GetValues<PositionSizingModel>();

    public decimal RatioInclusiveMinimum => BacktestConfigurationBounds.InclusiveRatioMinimum;
    public decimal RatioPositiveMinimum => BacktestConfigurationBounds.PositiveRatioMinimum;
    public decimal RatioInclusiveMaximum => BacktestConfigurationBounds.InclusiveRatioMaximum;
    public decimal RatioBelowUnitMaximum => BacktestConfigurationBounds.BelowUnitRatioMaximum;

    // Group B: capital and cost model.
    [ObservableProperty] private decimal _initialCapital;
    [ObservableProperty] private decimal _commissionFlat;
    [ObservableProperty] private decimal _commissionPerUnit;
    [ObservableProperty] private decimal _slippageRatio;
    [ObservableProperty] private PositionSizingModel _sizingModel;
    [ObservableProperty] private decimal _sizingParameter;

    // Group C: margin model.
    [ObservableProperty] private decimal _initialMarginRatio;
    [ObservableProperty] private decimal _maintenanceMarginRatio;
    [ObservableProperty] private decimal _liquidationPenaltyRatio;

    // Group D: report aggregation.
    [ObservableProperty] private decimal _annualRiskFreeRate;
    [ObservableProperty] private decimal _annualMar;
    [ObservableProperty] private int _annualPeriodsD1;
    [ObservableProperty] private int _annualPeriodsW1;
    [ObservableProperty] private int _annualPeriodsMN1;
    [ObservableProperty] private int _bootstrapSeed;
    [ObservableProperty] private int _bootstrapIterations;

    /// <summary>Why the last save failed at page level (I/O); empty otherwise. Per-item reasons are in the <c>*Error</c> properties.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasValidationMessage))] private string _validationMessage = string.Empty;

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    private IReadOnlyDictionary<string, string> _errors = new Dictionary<string, string>();

    /// <summary>Reason the item with the given <see cref="BacktestDefaultSettings"/> property name is rejected; empty when valid.</summary>
    private string ErrorOf(string field) => _errors.TryGetValue(field, out string? message) ? message : string.Empty;

    // Per-item reasons for the view (plain properties: bind reliably with compiled bindings).
    public string InitialCapitalError => ErrorOf(nameof(BacktestDefaultSettings.InitialCapital));
    public string CommissionFlatError => ErrorOf(nameof(BacktestDefaultSettings.CommissionFlat));
    public string CommissionPerUnitError => ErrorOf(nameof(BacktestDefaultSettings.CommissionPerUnit));
    public string SlippageRatioError => ErrorOf(nameof(BacktestDefaultSettings.SlippageRatio));
    public string SizingParameterError => ErrorOf(nameof(BacktestDefaultSettings.SizingParameter));
    public string InitialMarginRatioError => ErrorOf(nameof(BacktestDefaultSettings.InitialMarginRatio));
    public string MaintenanceMarginRatioError => ErrorOf(nameof(BacktestDefaultSettings.MaintenanceMarginRatio));
    public string LiquidationPenaltyRatioError => ErrorOf(nameof(BacktestDefaultSettings.LiquidationPenaltyRatio));
    public string AnnualRiskFreeRateError => ErrorOf(nameof(BacktestDefaultSettings.AnnualRiskFreeRate));
    public string AnnualMarError => ErrorOf(nameof(BacktestDefaultSettings.AnnualMAR));
    public string AnnualPeriodsError => ErrorOf(BacktestDefaultSettings.AnnualPeriodsErrorKey);
    public string BootstrapIterationsError => ErrorOf(nameof(BacktestDefaultSettings.BootstrapIterations));

    /// <summary>Whether every item is valid (the page can be saved).</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>Completes when the persisted values have been loaded into the page (the page shows factory defaults until then).</summary>
    public Task LoadTask { get; }

    public bool IsModified => !BuildRaw().ValueEquals(_snapshot);

    public BacktestSettingsViewModel(IBacktestDefaultSettingsManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        Apply(_snapshot);
        LoadTask = LoadAsync();
    }

    /// <summary>Designer fallback.</summary>
    public BacktestSettingsViewModel()
    {
        _manager = new DesignManager();
        Apply(_snapshot);
        LoadTask = Task.CompletedTask;
    }

    private sealed class DesignManager : IBacktestDefaultSettingsManager
    {
        public Task<BacktestDefaultSettingsLoadResult> LoadAsync() => Task.FromResult(
            new BacktestDefaultSettingsLoadResult(BacktestDefaultSettings.BuiltIn, BacktestDefaultSettingsLoadStatus.BuiltInFallback));

        public Task SaveAsync(BacktestDefaultSettings settings) => Task.CompletedTask;
    }

    private async Task LoadAsync()
    {
        try
        {
            BacktestDefaultSettingsLoadResult loaded = await _manager.LoadAsync().ConfigureAwait(true);
            _snapshot = loaded.Settings;
            Apply(_snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ValidationMessage = ex.Message;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Only edits of the editable properties refresh the derived state; the derived notifications raised by
        // RaiseDerived and the save-failure message are not edits.
        if (_isApplying || _isRaisingDerived || e.PropertyName is nameof(ValidationMessage) or nameof(HasValidationMessage))
        {
            return;
        }

        RaiseDerived();
    }

    /// <summary>Recomputes and announces everything derived from the editable values (IsModified, per-item reasons, IsValid).</summary>
    private void RaiseDerived()
    {
        _isRaisingDerived = true;
        try
        {
            OnPropertyChanged(nameof(IsModified));
            Validate();
        }
        finally
        {
            _isRaisingDerived = false;
        }
    }

    private void Validate()
    {
        _errors = BacktestDefaultSettings.FindErrors(BuildRaw());
        OnPropertyChanged(nameof(InitialCapitalError));
        OnPropertyChanged(nameof(CommissionFlatError));
        OnPropertyChanged(nameof(CommissionPerUnitError));
        OnPropertyChanged(nameof(SlippageRatioError));
        OnPropertyChanged(nameof(SizingParameterError));
        OnPropertyChanged(nameof(InitialMarginRatioError));
        OnPropertyChanged(nameof(MaintenanceMarginRatioError));
        OnPropertyChanged(nameof(LiquidationPenaltyRatioError));
        OnPropertyChanged(nameof(AnnualRiskFreeRateError));
        OnPropertyChanged(nameof(AnnualMarError));
        OnPropertyChanged(nameof(AnnualPeriodsError));
        OnPropertyChanged(nameof(BootstrapIterationsError));
        OnPropertyChanged(nameof(IsValid));
    }

    private BacktestDefaultSettings BuildRaw() => new()
    {
        SchemaVersion = BacktestDefaultSettings.CurrentSchemaVersion,
        InitialCapital = InitialCapital,
        CommissionFlat = CommissionFlat,
        CommissionPerUnit = CommissionPerUnit,
        SlippageRatio = SlippageRatio,
        SizingModel = SizingModel,
        SizingParameter = SizingParameter,
        InitialMarginRatio = InitialMarginRatio,
        MaintenanceMarginRatio = MaintenanceMarginRatio,
        LiquidationPenaltyRatio = LiquidationPenaltyRatio,
        AnnualRiskFreeRate = AnnualRiskFreeRate,
        AnnualMAR = AnnualMar,
        AnnualPeriodsByFrame = new Dictionary<TimeFrame, int>
        {
            [TimeFrame.D1] = AnnualPeriodsD1,
            [TimeFrame.W1] = AnnualPeriodsW1,
            [TimeFrame.MN1] = AnnualPeriodsMN1,
        },
        BootstrapSeed = BootstrapSeed,
        BootstrapIterations = BootstrapIterations,
    };

    /// <summary>Copies <paramref name="source"/> into the editable properties without per-property validation noise, then refreshes IsModified/validation once.</summary>
    private void Apply(BacktestDefaultSettings source)
    {
        _isApplying = true;
        try
        {
            InitialCapital = source.InitialCapital;
            CommissionFlat = source.CommissionFlat;
            CommissionPerUnit = source.CommissionPerUnit;
            SlippageRatio = source.SlippageRatio;
            SizingModel = source.SizingModel;
            SizingParameter = source.SizingParameter;
            InitialMarginRatio = source.InitialMarginRatio;
            MaintenanceMarginRatio = source.MaintenanceMarginRatio;
            LiquidationPenaltyRatio = source.LiquidationPenaltyRatio;
            AnnualRiskFreeRate = source.AnnualRiskFreeRate;
            AnnualMar = source.AnnualMAR;
            AnnualPeriodsD1 = source.AnnualPeriodsOf(TimeFrame.D1);
            AnnualPeriodsW1 = source.AnnualPeriodsOf(TimeFrame.W1);
            AnnualPeriodsMN1 = source.AnnualPeriodsOf(TimeFrame.MN1);
            BootstrapSeed = source.BootstrapSeed;
            BootstrapIterations = source.BootstrapIterations;
        }
        finally
        {
            _isApplying = false;
        }

        RaiseDerived();
    }

    public async Task SaveChangesAsync()
    {
        // Invalid values are already explained inside their own items; nothing is written.
        if (!IsValid) return;

        BacktestDefaultSettings validated;
        try
        {
            validated = BacktestDefaultSettings.SnapshotValidated(BuildRaw());
        }
        catch (ArgumentException ex)
        {
            ValidationMessage = ex.Message;
            return;
        }

        try
        {
            await _manager.SaveAsync(validated).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ValidationMessage = ex.Message;
            return;
        }

        _snapshot = validated;
        ValidationMessage = string.Empty;
        RaiseDerived();
    }

    public void RevertChanges() => Apply(_snapshot);

    public void ResetToDefault() => Apply(BacktestDefaultSettings.BuiltIn);
}
