using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Settings;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// GS-6, GS-8: Implementation of IChartSettingsManager with atomic persistence and thread safety.
/// </summary>
public sealed class ChartSettingsManager : IChartSettingsManager
{
    private readonly string _settingsFilePath;
    private readonly ILogger<ChartSettingsManager> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    
    // GS-8-2: Volatile immutable snapshot
    private volatile GlobalChartSettings _current = new();

    public event Action? SettingsChanged;

    public GlobalChartSettings Current => _current;

    public ChartSettingsManager(ILogger<ChartSettingsManager>? logger = null)
        : this(null, logger)
    {
    }

    public ChartSettingsManager(string? customPath, ILogger<ChartSettingsManager>? logger = null)
    {
        _logger = logger ?? NullLogger<ChartSettingsManager>.Instance;
        _settingsFilePath = customPath ?? Common.PathDiscovery.ResolveConfigPath("user_chart_settings.json");
    }

    /// <summary>
    /// GS-11: Updates settings and persists them. 
    /// Note: Throttling/Debouncing is handled at the ViewModel level for UI responsiveness,
    /// but this method ensures atomic replacement on disk.
    /// </summary>
    public async Task UpdateAsync(GlobalChartSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        
        var validated = settings.Validate();
        _current = validated;

        await SaveAsync(validated);
        
        // GS-12-1: Notify listeners (e.g., renderers to invalidate visual)
        SettingsChanged?.Invoke();
    }

    public void UpdatePreview(GlobalChartSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        
        _current = settings.Validate();
        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// GS-6-2: Atomic save using temporary file and replacement.
    /// </summary>
    private async Task SaveAsync(GlobalChartSettings settings)
    {
        await _fileLock.WaitAsync();
        try
        {
            string directory = Path.GetDirectoryName(_settingsFilePath)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            string tempPath = _settingsFilePath + ".tmp";
            
            // GS-7-2: Standard serialization options
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, options);
                await stream.FlushAsync();
            }

            // Verify written temporary file by reading it back
            using (var readStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                var verifiedSettings = await JsonSerializer.DeserializeAsync<GlobalChartSettings>(readStream, options);
                if (verifiedSettings == null)
                {
                    throw new InvalidOperationException("Read-back verification deserialization returned null.");
                }
            }

            // GS-6-2: Atomic replace
            if (File.Exists(_settingsFilePath))
            {
                File.Replace(tempPath, _settingsFilePath, _settingsFilePath + ".bak");
            }
            else
            {
                File.Move(tempPath, _settingsFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GS-15: Failed to save chart settings to {Path}", _settingsFilePath);
            // GS-15: Continue in memory-only mode if persistence fails
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// GS-6-3: Loads settings from disk with corruption recovery.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(_settingsFilePath))
        {
            _current = new GlobalChartSettings().Validate();
            SettingsChanged?.Invoke();
            await SaveAsync(_current);
            return;
        }

        await _fileLock.WaitAsync();
        try
        {
            using var stream = new FileStream(_settingsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var settings = await JsonSerializer.DeserializeAsync<GlobalChartSettings>(stream, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            _current = (settings ?? new GlobalChartSettings()).Validate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GS-6-3: Chart settings file corrupted or unreadable. Falling back to defaults.");
            
            // GS-6-3: Create backup of corrupted file
            try
            {
                string backupPath = _settingsFilePath + ".corrupted";
                if (File.Exists(_settingsFilePath)) File.Copy(_settingsFilePath, backupPath, true);
            }
            catch { /* Ignore secondary failures */ }

            _current = new GlobalChartSettings().Validate();
        }
        finally
        {
            _fileLock.Release();
        }

        SettingsChanged?.Invoke();
    }
}
