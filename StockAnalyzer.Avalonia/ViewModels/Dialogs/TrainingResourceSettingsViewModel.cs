using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>Exclusive editor for the training-only resource preview.</summary>
public sealed class TrainingResourceSettingsViewModel : ViewModelBase,
    ISettingsPageViewModel, ITransactionalSettingsPage, IDisposable
{
    private readonly ITrainingResourceSettings _settings;
    private readonly object _owner = new();
    private bool _ownsPreview;
    private bool _initializing;
    private bool _isSaveInProgress;
    private bool _channelsEnabled;
    private bool _tensorEnabled;
    private bool _samplesEnabled;
    private decimal? _channelsValue;
    private decimal? _tensorValue;
    private decimal? _samplesValue;
    private string? _validationMessage;

    public TrainingResourceSettingsViewModel(ITrainingResourceSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeFrom(_settings.SavedSnapshot);
        Activate();
    }

    public string TitleKey => "Settings_TrainingWizard";
    public string IconKey => "SettingsAdvIcon";
    public int Minimum => TrainingResourceOverrides.Minimum;
    public int MaximumChannels => TrainingResourceOverrides.MaximumChannels;
    public int MaximumTensorSizeMiB => TrainingResourceOverrides.MaximumTensorSizeMiB;
    public int MaximumSamples => TrainingResourceOverrides.MaximumSamples;
    public bool IsEditable => _ownsPreview && !_isSaveInProgress;
    public bool HasOwnershipWarning => !_ownsPreview;
    public bool IsSaveInProgress => _isSaveInProgress;
    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);
    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value)) OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool ChannelsEnabled
    {
        get => _channelsEnabled;
        set
        {
            if (!IsEditable || !SetProperty(ref _channelsEnabled, value)) return;
            if (value && ChannelsValue is null) ChannelsValue = MaximumChannels;
            RefreshPreview();
        }
    }

    public bool TensorEnabled
    {
        get => _tensorEnabled;
        set
        {
            if (!IsEditable || !SetProperty(ref _tensorEnabled, value)) return;
            if (value && TensorValue is null) TensorValue = MaximumTensorSizeMiB;
            RefreshPreview();
        }
    }

    public bool SamplesEnabled
    {
        get => _samplesEnabled;
        set
        {
            if (!IsEditable || !SetProperty(ref _samplesEnabled, value)) return;
            if (value && SamplesValue is null) SamplesValue = MaximumSamples;
            RefreshPreview();
        }
    }

    public decimal? ChannelsValue
    {
        get => _channelsValue;
        set { if (IsEditable && SetProperty(ref _channelsValue, value)) RefreshPreview(); }
    }

    public decimal? TensorValue
    {
        get => _tensorValue;
        set { if (IsEditable && SetProperty(ref _tensorValue, value)) RefreshPreview(); }
    }

    public decimal? SamplesValue
    {
        get => _samplesValue;
        set { if (IsEditable && SetProperty(ref _samplesValue, value)) RefreshPreview(); }
    }

    public bool IsModified
    {
        get
        {
            if (!_ownsPreview) return false;
            return !TryBuild(out var value) || value != _settings.SavedSnapshot;
        }
    }

    public void Activate()
    {
        if (_ownsPreview || !_settings.TryAcquirePreview(_owner)) return;
        _ownsPreview = true;
        InitializeFrom(_settings.SavedSnapshot);
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(HasOwnershipWarning));
    }

    private void InitializeFrom(TrainingResourceOverrides value)
    {
        _initializing = true;
        _channelsEnabled = value.MaxChannels.HasValue;
        _tensorEnabled = value.MaxTensorSizeMiB.HasValue;
        _samplesEnabled = value.MaxSamples.HasValue;
        _channelsValue = value.MaxChannels ?? MaximumChannels;
        _tensorValue = value.MaxTensorSizeMiB ?? MaximumTensorSizeMiB;
        _samplesValue = value.MaxSamples ?? MaximumSamples;
        _initializing = false;
        ValidationMessage = _settings.LoadError;
        OnPropertyChanged(nameof(ChannelsEnabled));
        OnPropertyChanged(nameof(TensorEnabled));
        OnPropertyChanged(nameof(SamplesEnabled));
        OnPropertyChanged(nameof(ChannelsValue));
        OnPropertyChanged(nameof(TensorValue));
        OnPropertyChanged(nameof(SamplesValue));
        OnPropertyChanged(nameof(IsModified));
    }

    private bool TryBuild(out TrainingResourceOverrides value)
    {
        value = new();
        if (!TryInteger(ChannelsEnabled, ChannelsValue, MaximumChannels, out var channels) ||
            !TryInteger(TensorEnabled, TensorValue, MaximumTensorSizeMiB, out var tensor) ||
            !TryInteger(SamplesEnabled, SamplesValue, MaximumSamples, out var samples)) return false;
        value = new TrainingResourceOverrides(channels, tensor, samples);
        return true;
    }

    private static bool TryInteger(bool enabled, decimal? raw, int maximum, out int? result)
    {
        result = null;
        if (!enabled) return true;
        if (raw is not { } number || number != decimal.Truncate(number) ||
            number < TrainingResourceOverrides.Minimum || number > maximum) return false;
        result = (int)number;
        return true;
    }

    private void RefreshPreview()
    {
        if (_initializing || !_ownsPreview) return;
        if (!TryBuild(out var value))
        {
            ValidationMessage = LocalizationManager.Instance["Settings_TrainingWizard_InvalidNumber"];
        }
        else
        {
            try
            {
                _settings.SetPreview(_owner, value);
                ValidationMessage = _settings.LoadError;
            }
            catch (InvalidOperationException ex)
            {
                ValidationMessage = ex.Message;
            }
        }
        OnPropertyChanged(nameof(IsModified));
    }

    public async Task<bool> TrySaveChangesAsync()
    {
        if (!IsEditable || !TryBuild(out var value))
        {
            ValidationMessage = LocalizationManager.Instance["Settings_TrainingWizard_InvalidNumber"];
            return false;
        }
        _isSaveInProgress = true;
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsSaveInProgress));
        try
        {
            _settings.SetPreview(_owner, value);
            await _settings.SaveAsync(_owner);
            ValidationMessage = null;
            OnPropertyChanged(nameof(IsModified));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ValidationMessage = ex.Message;
            OnPropertyChanged(nameof(IsModified));
            return false;
        }
        finally
        {
            _isSaveInProgress = false;
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(IsSaveInProgress));
        }
    }

    public async Task SaveChangesAsync()
    {
        if (!await TrySaveChangesAsync())
            throw new InvalidOperationException(ValidationMessage ?? "Training resource settings could not be saved.");
    }

    public void RevertChanges()
    {
        if (!_ownsPreview || _isSaveInProgress) return;
        _settings.SetPreview(_owner, _settings.SavedSnapshot);
        InitializeFrom(_settings.SavedSnapshot);
    }

    public void RestoreSavedPreviewOnClose() => RevertChanges();

    public void ResetToDefault()
    {
        if (!_ownsPreview || _isSaveInProgress) return;
        InitializeFrom(new TrainingResourceOverrides());
        RefreshPreview();
    }

    public void Dispose()
    {
        if (!_ownsPreview) return;
        _settings.ReleasePreview(_owner);
        _ownsPreview = false;
    }
}
