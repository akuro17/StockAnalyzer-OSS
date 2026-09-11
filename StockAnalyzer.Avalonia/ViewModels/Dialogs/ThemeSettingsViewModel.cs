using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Avalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class ThemeSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
{
    private readonly IThemeManager? _themeManager;
    private readonly ITemplateService? _templateService;
    private readonly IDialogService? _dialogService;
    private readonly ILogger<ThemeSettingsViewModel> _logger;
    private IReadOnlyDictionary<ThemeColorKey, IndicatorColor> _snapshot = new Dictionary<ThemeColorKey, IndicatorColor>();
    private AppThemeMode _snapshotMode;

    private IndicatorColor _snapBackgroundColor;
    private IndicatorColor _snapGridColor;
    private IndicatorColor _snapAxisColor;
    private IndicatorColor _snapCrosshairColor;
    private IndicatorColor _snapVolumeUpColor;
    private IndicatorColor _snapVolumeDownColor;
    private IndicatorColor _snapRulerFillColor;
    private IndicatorColor _snapRulerStrokeColor;
    private IndicatorColor _snapShellBackgroundColor;
    private IndicatorColor _snapShellTextColor;
    private IndicatorColor _snapShellSecondaryTextColor;
    private IndicatorColor _snapShellAccentColor;
    private IndicatorColor _snapShellBorderColor;
    private IndicatorColor _snapSemanticPlusColor;
    private IndicatorColor _snapSemanticMinusColor;
    private IndicatorColor _snapSemanticNeutralColor;
    private IndicatorColor _snapButtonBackground;
    private IndicatorColor _snapButtonText;
    private IndicatorColor _snapButtonHover;
    private IndicatorColor _snapButtonPressed;

    private bool _isDisposed;

    // Performance control
    private DateTime _lastUpdate = DateTime.MinValue;
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(16);
    private CancellationTokenSource? _throttleCts;

    public string TitleKey => "Settings_Theme";
    public string IconKey => "SettingsThemeIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private AppThemeMode _selectedPreset = AppThemeMode.Dark;

    [ObservableProperty]
    private string _newThemeName = string.Empty;

    [ObservableProperty]
    private ThemeTemplate? _selectedThemeTemplate;

    public ObservableCollection<ThemeTemplate> SavedThemes { get; } = new();

    #region Color Properties (14 Items)

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _backgroundColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _gridColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _axisColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _crosshairColor;
    
    
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _volumeUpColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _volumeDownColor;
    
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _rulerFillColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _rulerStrokeColor;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _shellBackgroundColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _shellTextColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _shellSecondaryTextColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _shellAccentColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _shellBorderColor;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _semanticPlusColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _semanticMinusColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _semanticNeutralColor;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _buttonBackground;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _buttonText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _buttonHover;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _buttonPressed;

    #endregion

    public ObservableCollection<AppThemeMode> AvailablePresets { get; } = new()
    {
        AppThemeMode.System,
        AppThemeMode.Light,
        AppThemeMode.Dark
    };

    public ThemeSettingsViewModel(
        IThemeManager themeManager,
        ITemplateService? templateService = null,
        IDialogService? dialogService = null,
        ILogger<ThemeSettingsViewModel>? logger = null)
    {
        _themeManager = themeManager;
        _templateService = templateService;
        _dialogService = dialogService;
        _logger = logger ?? NullLogger<ThemeSettingsViewModel>.Instance;
        TakeSnapshot();
        InitializeFromSnapshot();
        _ = LoadSavedThemesAsync();
    }

    public ThemeSettingsViewModel()
    {
        _logger = NullLogger<ThemeSettingsViewModel>.Instance;
        _snapshotMode = AppThemeMode.Dark;
        InitializeFromSnapshot();
    }

    private void TakeSnapshot()
    {
        if (_themeManager == null) return;
        _snapshot = _themeManager.GetCurrentColors();
        _snapshotMode = _themeManager.CurrentMode;

        if (_snapshot.Count > 0)
        {
            _snapBackgroundColor = _snapshot[ThemeColorKey.Background];
            _snapGridColor        = _snapshot[ThemeColorKey.Grid];
            _snapAxisColor        = _snapshot[ThemeColorKey.Axis];
            _snapCrosshairColor   = _snapshot[ThemeColorKey.Crosshair];
            _snapVolumeUpColor    = _snapshot[ThemeColorKey.VolumeUp];
            _snapVolumeDownColor  = _snapshot[ThemeColorKey.VolumeDown];
            _snapRulerFillColor   = _snapshot[ThemeColorKey.RulerFill];
            _snapRulerStrokeColor = _snapshot[ThemeColorKey.RulerStroke];
            _snapShellBackgroundColor = _snapshot[ThemeColorKey.ShellBackground];
            _snapShellTextColor       = _snapshot[ThemeColorKey.ShellText];
            _snapShellSecondaryTextColor = _snapshot.TryGetValue(ThemeColorKey.ShellSecondaryText, out var secVal) ? secVal : _snapshot[ThemeColorKey.ShellText];
            _snapShellAccentColor     = _snapshot[ThemeColorKey.ShellAccent];
            _snapShellBorderColor     = _snapshot[ThemeColorKey.ShellBorder];
            _snapSemanticPlusColor    = _snapshot[ThemeColorKey.SemanticPlus];
            _snapSemanticMinusColor   = _snapshot[ThemeColorKey.SemanticMinus];
            _snapSemanticNeutralColor = _snapshot[ThemeColorKey.SemanticNeutral];
            _snapButtonBackground     = _snapshot[ThemeColorKey.ButtonBackground];
            _snapButtonText           = _snapshot[ThemeColorKey.ButtonText];
            _snapButtonHover          = _snapshot[ThemeColorKey.ButtonHover];
            _snapButtonPressed        = _snapshot[ThemeColorKey.ButtonPressed];
        }
    }

    private void InitializeFromSnapshot()
    {
        _selectedPreset = _snapshotMode;

        SyncColorsFromMap(_snapshot);
        OnPropertyChanged(nameof(IsModified));
    }

    public bool IsModified
    {
        get
        {
            if (_themeManager == null) return false;

            if (SelectedPreset != _snapshotMode) return true;

            return _backgroundColor != _snapBackgroundColor ||
                   _gridColor        != _snapGridColor        ||
                   _axisColor        != _snapAxisColor        ||
                   _crosshairColor   != _snapCrosshairColor   ||
                   _volumeUpColor    != _snapVolumeUpColor    ||
                   _volumeDownColor  != _snapVolumeDownColor  ||
                   _rulerFillColor   != _snapRulerFillColor   ||
                   _rulerStrokeColor != _snapRulerStrokeColor ||
                   _shellBackgroundColor != _snapShellBackgroundColor ||
                   _shellTextColor       != _snapShellTextColor       ||
                   _shellSecondaryTextColor != _snapShellSecondaryTextColor ||
                   _shellAccentColor     != _snapShellAccentColor     ||
                   _shellBorderColor     != _snapShellBorderColor     ||
                   _semanticPlusColor    != _snapSemanticPlusColor    ||
                   _semanticMinusColor   != _snapSemanticMinusColor   ||
                   _semanticNeutralColor != _snapSemanticNeutralColor ||
                   _buttonBackground     != _snapButtonBackground     ||
                   _buttonText           != _snapButtonText           ||
                   _buttonHover          != _snapButtonHover          ||
                   _buttonPressed        != _snapButtonPressed;
        }
    }

    partial void OnSelectedPresetChanged(AppThemeMode value)
    {
        if (_isDisposed || _themeManager == null) return;

        _themeManager.SetThemeMode(value);
        SyncColorsFromMap(_themeManager.GetCurrentColors());
    }

    private void ApplyColorChange(ThemeColorKey key, IndicatorColor color)
    {
        if (_themeManager == null) return;

        var now = DateTime.UtcNow;
        if (now - _lastUpdate < ThrottleInterval)
        {
            _throttleCts?.Cancel();
            _throttleCts = new CancellationTokenSource();
            var token = _throttleCts.Token;
            _ = Task.Delay(ThrottleInterval, token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !_isDisposed && _themeManager != null)
                {
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _themeManager.UpdateSingleColor(key, color);
                    });
                }
            }, TaskScheduler.Default);
            return;
        }

        _throttleCts?.Cancel();
        _lastUpdate = now;
        _themeManager.UpdateSingleColor(key, color);
    }

    #region Change Notification Hooks

    partial void OnBackgroundColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.Background, value);
    partial void OnGridColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.Grid, value);
    partial void OnAxisColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.Axis, value);
    partial void OnCrosshairColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.Crosshair, value);
    partial void OnVolumeUpColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.VolumeUp, value);
    partial void OnVolumeDownColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.VolumeDown, value);
    partial void OnRulerFillColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.RulerFill, value);
    partial void OnRulerStrokeColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.RulerStroke, value);
    partial void OnShellBackgroundColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ShellBackground, value);
    partial void OnShellTextColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ShellText, value);
    partial void OnShellSecondaryTextColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ShellSecondaryText, value);
    partial void OnShellAccentColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ShellAccent, value);
    partial void OnShellBorderColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ShellBorder, value);
    partial void OnSemanticPlusColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.SemanticPlus, value);
    partial void OnSemanticMinusColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.SemanticMinus, value);
    partial void OnSemanticNeutralColorChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.SemanticNeutral, value);
    partial void OnButtonBackgroundChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ButtonBackground, value);
    partial void OnButtonTextChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ButtonText, value);
    partial void OnButtonHoverChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ButtonHover, value);
    partial void OnButtonPressedChanged(IndicatorColor value) => ApplyColorChange(ThemeColorKey.ButtonPressed, value);

    #endregion

    public async Task SaveChangesAsync()
    {
        if (_themeManager == null) return;
        await _themeManager.SaveAsync();
        TakeSnapshot();
        OnPropertyChanged(nameof(IsModified));
    }

    public void RevertChanges()
    {
        if (_isDisposed || _themeManager == null) return;
        _themeManager.SetThemeMode(_snapshotMode);
        _themeManager.ChangeTheme(_themeManager.CurrentMode == AppThemeMode.Light ? ThemeColors.Light : ThemeColors.Dark); // Restore base
        
        // Restore individual colors from snapshot
        foreach (var kvp in _snapshot)
        {
            _themeManager.UpdateSingleColor(kvp.Key, kvp.Value);
        }

        InitializeFromSnapshot();
    }

    public void ResetToDefault()
    {
        if (_isDisposed || _themeManager == null) return;
        var preset = SelectedPreset == AppThemeMode.Dark ? ThemeColors.Dark : ThemeColors.Light;
        _themeManager.ChangeTheme(preset);
        SyncColorsFromMap(_themeManager.GetCurrentColors());
        OnPropertyChanged(nameof(IsModified));
    }

    [RelayCommand]
    private void ResetColors() => ResetToDefault();

    [RelayCommand]
    private async Task SaveTheme()
    {
        await SaveChangesAsync();
    }

    public async Task LoadSavedThemesAsync()
    {
        if (_templateService == null) return;
        try
        {
            var templates = await _templateService.GetAllAsync<ThemeTemplate>(TemplateType.Theme);
            SavedThemes.Clear();
            foreach (var template in templates)
            {
                SavedThemes.Add(template);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load saved theme templates");
        }
    }

    [RelayCommand]
    private async Task SaveCustomThemeAsync()
    {
        if (string.IsNullOrWhiteSpace(NewThemeName)) return;
        var trimmedName = NewThemeName.Trim();

        var existing = SavedThemes.FirstOrDefault(t => string.Equals(t.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
        if (existing != null && _dialogService != null)
        {
            var title = LocalizationManager.Instance["Dialog_Confirm_Title"] ?? "Confirmation";
            var message = string.Format(
                LocalizationManager.Instance["Settings_Theme_OverwriteConfirm"] ?? "A theme named '{0}' already exists. Do you want to overwrite it?",
                trimmedName);

            var confirmed = await _dialogService.ShowConfirmationAsync(title, message);
            if (!confirmed) return;
        }

        var currentColors = BuildCurrentThemeColors();
        var template = existing ?? new ThemeTemplate
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            SchemaVersion = 1,
            CreatedAt = DateTime.UtcNow
        };

        template.Colors = currentColors;
        template.UpdatedAt = DateTime.UtcNow;

        if (_templateService != null)
        {
            try
            {
                await _templateService.SaveAsync(template);
                if (existing == null)
                {
                    SavedThemes.Add(template);
                }
                else
                {
                    var index = SavedThemes.IndexOf(existing);
                    if (index >= 0) SavedThemes[index] = template;
                }
                NewThemeName = string.Empty;
                SelectedThemeTemplate = template;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save theme template '{Name}'", trimmedName);
            }
        }
        else
        {
            if (existing == null) SavedThemes.Add(template);
            NewThemeName = string.Empty;
            SelectedThemeTemplate = template;
        }
    }

    [RelayCommand]
    private void ApplyCustomTheme(ThemeTemplate? template)
    {
        if (template?.Colors == null || _themeManager == null) return;
        SelectedThemeTemplate = template;
        _themeManager.ChangeTheme(template.Colors);
        SyncColorsFromMap(_themeManager.GetCurrentColors());
        OnPropertyChanged(nameof(IsModified));
    }

    [RelayCommand]
    private async Task DeleteCustomThemeAsync(ThemeTemplate? template)
    {
        if (template == null) return;
        if (_dialogService != null)
        {
            var title = LocalizationManager.Instance["Dialog_Confirm_Title"] ?? "Confirmation";
            var message = string.Format(
                LocalizationManager.Instance["Settings_Theme_DeleteConfirm"] ?? "Are you sure you want to delete the theme '{0}'?",
                template.Name);

            var confirmed = await _dialogService.ShowConfirmationAsync(title, message);
            if (!confirmed) return;
        }

        if (_templateService != null)
        {
            try
            {
                await _templateService.DeleteAsync(TemplateType.Theme, template.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete theme template '{Id}'", template.Id);
            }
        }

        SavedThemes.Remove(template);
        if (SelectedThemeTemplate == template)
        {
            SelectedThemeTemplate = null;
        }
    }

    private ThemeColors BuildCurrentThemeColors()
    {
        var baseColors = _themeManager?.CurrentTheme ?? (SelectedPreset == AppThemeMode.Light ? ThemeColors.Light : ThemeColors.Dark);
        return baseColors with
        {
            ChartBackground = BackgroundColor,
            GridLine = GridColor,
            AxisText = AxisColor,
            Crosshair = CrosshairColor,
            VolumeUp = VolumeUpColor,
            VolumeDown = VolumeDownColor,
            RulerFill = RulerFillColor,
            RulerStroke = RulerStrokeColor,
            ShellBackground = ShellBackgroundColor,
            ShellText = ShellTextColor,
            ShellSecondaryText = ShellSecondaryTextColor,
            ShellAccent = ShellAccentColor,
            ShellBorder = ShellBorderColor,
            SemanticPlus = SemanticPlusColor,
            SemanticMinus = SemanticMinusColor,
            SemanticNeutral = SemanticNeutralColor,
            ButtonBackground = ButtonBackground,
            ButtonText = ButtonText,
            ButtonHover = ButtonHover,
            ButtonPressed = ButtonPressed
        };
    }

    private void SyncColorsFromMap(IReadOnlyDictionary<ThemeColorKey, IndicatorColor> map)
    {
        if (map.Count == 0) return;

        // Use backing fields to prevent triggering ApplyColorChange during sync
        _backgroundColor = map[ThemeColorKey.Background];
        _gridColor        = map[ThemeColorKey.Grid];
        _axisColor        = map[ThemeColorKey.Axis];
        _crosshairColor   = map[ThemeColorKey.Crosshair];
        _volumeUpColor    = map[ThemeColorKey.VolumeUp];
        _volumeDownColor  = map[ThemeColorKey.VolumeDown];
        _rulerFillColor   = map[ThemeColorKey.RulerFill];
        _rulerStrokeColor = map[ThemeColorKey.RulerStroke];
        _shellBackgroundColor = map[ThemeColorKey.ShellBackground];
        _shellTextColor       = map[ThemeColorKey.ShellText];
        _shellSecondaryTextColor = map.TryGetValue(ThemeColorKey.ShellSecondaryText, out var secCol) ? secCol : map[ThemeColorKey.ShellText];
        _shellAccentColor     = map[ThemeColorKey.ShellAccent];
        _shellBorderColor     = map[ThemeColorKey.ShellBorder];
        _semanticPlusColor    = map[ThemeColorKey.SemanticPlus];
        _semanticMinusColor   = map[ThemeColorKey.SemanticMinus];
        _semanticNeutralColor = map[ThemeColorKey.SemanticNeutral];
        _buttonBackground     = map[ThemeColorKey.ButtonBackground];
        _buttonText           = map[ThemeColorKey.ButtonText];
        _buttonHover          = map[ThemeColorKey.ButtonHover];
        _buttonPressed        = map[ThemeColorKey.ButtonPressed];

        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(GridColor));
        OnPropertyChanged(nameof(AxisColor));
        OnPropertyChanged(nameof(CrosshairColor));
        OnPropertyChanged(nameof(VolumeUpColor));
        OnPropertyChanged(nameof(VolumeDownColor));
        OnPropertyChanged(nameof(RulerFillColor));
        OnPropertyChanged(nameof(RulerStrokeColor));
        OnPropertyChanged(nameof(ShellBackgroundColor));
        OnPropertyChanged(nameof(ShellTextColor));
        OnPropertyChanged(nameof(ShellSecondaryTextColor));
        OnPropertyChanged(nameof(ShellAccentColor));
        OnPropertyChanged(nameof(ShellBorderColor));
        OnPropertyChanged(nameof(SemanticPlusColor));
        OnPropertyChanged(nameof(SemanticMinusColor));
        OnPropertyChanged(nameof(SemanticNeutralColor));
        OnPropertyChanged(nameof(ButtonBackground));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonHover));
        OnPropertyChanged(nameof(ButtonPressed));
    }
    
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        _throttleCts?.Cancel();
        _throttleCts?.Dispose();
        _throttleCts = null;

        GC.SuppressFinalize(this);
    }
}
