using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Thread-safe service responsible for persisting and managing registered Source Indicators
/// independently of active chart indicators. Persists to Data/SourceIndicators/source_indicators.json.
/// </summary>
public class SourceIndicatorService : ISourceIndicatorService, IDisposable
{
    private const string FileName = "source_indicators.json";
    private readonly string _filePath;
    private readonly ILogger<SourceIndicatorService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly List<CoreIndicatorSettings> _cache = new();
    private bool _isLoaded;
    private bool _isDisposed;

    public SourceIndicatorService(string? customFilePath = null, ILogger<SourceIndicatorService>? logger = null)
    {
        _logger = logger ?? NullLogger<SourceIndicatorService>.Instance;
        _filePath = customFilePath ?? PathDiscovery.ResolveSourceIndicatorsPath(FileName);
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new IndicatorColorJsonConverter() }
        };

        // Eager initial load of cache if file exists
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var items = JsonSerializer.Deserialize<List<CoreIndicatorSettings>>(json, _jsonOptions);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        DefaultCoreIndicatorSettings.AutoHeal(item);
                        _cache.Add(item);
                    }
                    _isLoaded = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize source indicators cache from disk");
        }
    }

    private string GetFilePath() => _filePath;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoreIndicatorSettings>> GetSourceIndicatorsAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return _cache.Select(i => i.Snapshot()).ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CoreIndicatorSettings> GetSourceIndicators()
    {
        lock (_cache)
        {
            return _cache.Select(i => i.Snapshot()).ToList();
        }
    }

    /// <inheritdoc />
    public CoreIndicatorSettings? GetSourceIndicator(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_cache)
        {
            return _cache.FirstOrDefault(i => i.Id == id)?.Snapshot();
        }
    }

    /// <inheritdoc />
    public async Task SaveSourceIndicatorAsync(CoreIndicatorSettings indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);

        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var item = indicator.Snapshot();
            if (string.IsNullOrEmpty(item.Id))
            {
                item.Id = Guid.NewGuid().ToString();
            }

            int index = _cache.FindIndex(i => i.Id == item.Id);
            if (index >= 0)
            {
                _cache[index] = item;
            }
            else
            {
                _cache.Add(item);
            }

            await PersistToDiskAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSourceIndicatorAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            int index = _cache.FindIndex(i => i.Id == id);
            if (index >= 0)
            {
                _cache.RemoveAt(index);
                await PersistToDiskAsync().ConfigureAwait(false);
                return true;
            }
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isLoaded) return;

            var filePath = GetFilePath();
            if (File.Exists(filePath))
            {
                var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                var items = JsonSerializer.Deserialize<List<CoreIndicatorSettings>>(json, _jsonOptions);
                if (items != null)
                {
                    _cache.Clear();
                    foreach (var item in items)
                    {
                        DefaultCoreIndicatorSettings.AutoHeal(item);
                        _cache.Add(item);
                    }
                }
            }
            _isLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load source indicators from {FilePath}", GetFilePath());
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task PersistToDiskAsync()
    {
        var filePath = GetFilePath();
        var tempPath = filePath + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(_cache, _jsonOptions);

        try
        {
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
