using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Implements IThemeManager to store and notify about application theme changes.
/// </summary>
public class ThemeManager : IThemeManager
{
    private static readonly string ThemeFilePath = PathDiscovery.ResolveConfigPath("user_theme.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new IndicatorColorJsonConverter() }
    };

    private readonly IThemeVariantDispatcher? _dispatcher;
    private AppThemeMode _currentMode = AppThemeMode.Dark;
    private ThemeColors _currentTheme = ThemeColors.Dark;
    private readonly Dictionary<ThemeColorKey, IndicatorColor> _colorsCache = new();

    public ThemeManager(IThemeVariantDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
        UpdateCache();
    }

    public ThemeColors CurrentTheme
    {
        get => _currentTheme;
        private set
        {
            if (_currentTheme != value)
            {
                _currentTheme = value;
                UpdateCache();
                OnPropertyChanged();
            }
        }
    }

    public AppThemeMode CurrentMode
    {
        get => _currentMode;
        private set
        {
            if (_currentMode != value)
            {
                _currentMode = value;
                OnPropertyChanged();
            }
        }
    }

    private void UpdateCache()
    {
        _colorsCache[ThemeColorKey.Background]  = _currentTheme.ChartBackground;
        _colorsCache[ThemeColorKey.Grid]        = _currentTheme.GridLine;
        _colorsCache[ThemeColorKey.Axis]        = _currentTheme.AxisText;
        _colorsCache[ThemeColorKey.Crosshair]   = _currentTheme.Crosshair;
        _colorsCache[ThemeColorKey.Bullish]     = _currentTheme.Bullish;
        _colorsCache[ThemeColorKey.Bearish]     = _currentTheme.Bearish;
        _colorsCache[ThemeColorKey.Neutral]     = _currentTheme.Neutral;
        _colorsCache[ThemeColorKey.VolumeUp]    = _currentTheme.VolumeUp;
        _colorsCache[ThemeColorKey.VolumeDown]  = _currentTheme.VolumeDown;
        _colorsCache[ThemeColorKey.RulerFill]   = _currentTheme.RulerFill;
        _colorsCache[ThemeColorKey.RulerStroke] = _currentTheme.RulerStroke;
        _colorsCache[ThemeColorKey.ShellBackground] = _currentTheme.ShellBackground;
        _colorsCache[ThemeColorKey.ShellText]       = _currentTheme.ShellText;
        _colorsCache[ThemeColorKey.ShellSecondaryText] = _currentTheme.ShellSecondaryText;
        _colorsCache[ThemeColorKey.ShellAccent]     = _currentTheme.ShellAccent;
        _colorsCache[ThemeColorKey.ShellBorder]     = _currentTheme.ShellBorder;
        _colorsCache[ThemeColorKey.SemanticPlus]    = _currentTheme.SemanticPlus;
        _colorsCache[ThemeColorKey.SemanticMinus]   = _currentTheme.SemanticMinus;
        _colorsCache[ThemeColorKey.SemanticNeutral] = _currentTheme.SemanticNeutral;
        _colorsCache[ThemeColorKey.ButtonBackground] = _currentTheme.ButtonBackground;
        _colorsCache[ThemeColorKey.ButtonText]       = _currentTheme.ButtonText;
        _colorsCache[ThemeColorKey.ButtonHover]      = _currentTheme.ButtonHover;
        _colorsCache[ThemeColorKey.ButtonPressed]    = _currentTheme.ButtonPressed;
    }

    public void ChangeTheme(ThemeColors newTheme)
    {
        ArgumentNullException.ThrowIfNull(newTheme);
        CurrentTheme = newTheme;
    }

    public void UpdateSingleColor(ThemeColorKey key, IndicatorColor color)
    {
        var updated = key switch
        {
            ThemeColorKey.Background  => CurrentTheme with { ChartBackground = color },
            ThemeColorKey.Grid        => CurrentTheme with { GridLine = color },
            ThemeColorKey.Axis        => CurrentTheme with { AxisText = color },
            ThemeColorKey.Crosshair   => CurrentTheme.WithDerivedCrosshair(color),
            ThemeColorKey.Bullish     => CurrentTheme with { Bullish = color, BullishWick = color },
            ThemeColorKey.Bearish     => CurrentTheme with { Bearish = color, BearishWick = color },
            ThemeColorKey.Neutral     => CurrentTheme with { Neutral = color },
            ThemeColorKey.VolumeUp    => CurrentTheme with { VolumeUp = color },
            ThemeColorKey.VolumeDown  => CurrentTheme with { VolumeDown = color },
            ThemeColorKey.RulerFill   => CurrentTheme with { RulerFill = color },
            ThemeColorKey.RulerStroke => CurrentTheme with { RulerStroke = color },
            ThemeColorKey.ShellBackground => CurrentTheme with { ShellBackground = color },
            ThemeColorKey.ShellText       => CurrentTheme with { ShellText = color },
            ThemeColorKey.ShellSecondaryText => CurrentTheme with { ShellSecondaryText = color },
            ThemeColorKey.ShellAccent     => CurrentTheme with { ShellAccent = color },
            ThemeColorKey.ShellBorder     => CurrentTheme with { ShellBorder = color },
            ThemeColorKey.SemanticPlus    => CurrentTheme with { SemanticPlus = color },
            ThemeColorKey.SemanticMinus   => CurrentTheme with { SemanticMinus = color },
            ThemeColorKey.SemanticNeutral => CurrentTheme with { SemanticNeutral = color },
            ThemeColorKey.ButtonBackground => CurrentTheme with { ButtonBackground = color },
            ThemeColorKey.ButtonText       => CurrentTheme with { ButtonText = color },
            ThemeColorKey.ButtonHover      => CurrentTheme with { ButtonHover = color },
            ThemeColorKey.ButtonPressed    => CurrentTheme with { ButtonPressed = color },
            _ => CurrentTheme
        };

        ChangeTheme(updated);
    }

    public IReadOnlyDictionary<ThemeColorKey, IndicatorColor> GetCurrentColors()
    {
        return _colorsCache;
    }

    public void SetThemeMode(AppThemeMode mode)
    {
        if (CurrentMode == mode) return;

        CurrentMode = mode;

        // Resolve actual mode if System is selected
        AppThemeMode actualMode = mode;
        if (mode == AppThemeMode.System && _dispatcher != null)
        {
            actualMode = _dispatcher.GetActualThemeMode();
        }

        // Sync Skia Colors only if theme mode actually changed
        bool targetIsDark = (actualMode == AppThemeMode.Dark);
        if (CurrentTheme.IsDark != targetIsDark)
        {
            ChangeTheme(targetIsDark ? ThemeColors.Dark : ThemeColors.Light);
        }

        // Notify UI Dispatcher (Avalonia)
        _dispatcher?.ApplyTheme(mode);

        // Persist Settings
        _ = SaveAsync();
    }

    public async Task SaveAsync()
    {
        string tempPath = ThemeFilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(ThemeFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var data = new ThemePersistenceData
            {
                Mode = CurrentMode,
                Colors = CurrentTheme
            };

            // Write to temporary file
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
            }

            // Atomically replace the target file
            if (File.Exists(ThemeFilePath))
            {
                File.Replace(tempPath, ThemeFilePath, ThemeFilePath + ".bak");
            }
            else
            {
                File.Move(tempPath, ThemeFilePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save theme atomically: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
            }
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(ThemeFilePath))
            {
                // Set default if no file
                SetThemeMode(AppThemeMode.Dark);
                return;
            }

            using var stream = File.OpenRead(ThemeFilePath);
            var data = await JsonSerializer.DeserializeAsync<ThemePersistenceData>(stream, JsonOptions);
            if (data != null)
            {
                CurrentMode = data.Mode;
                CurrentTheme = data.Colors ?? ThemeColors.Dark;
                
                // Trigger initial apply (without saving again)
                _dispatcher?.ApplyTheme(CurrentMode);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load theme: {ex.Message}");
            SetThemeMode(AppThemeMode.Dark);
        }
    }

    private class ThemePersistenceData
    {
        public AppThemeMode Mode { get; set; }
        public ThemeColors? Colors { get; set; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
