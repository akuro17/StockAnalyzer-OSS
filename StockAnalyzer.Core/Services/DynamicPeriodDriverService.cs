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
/// Thread-safe service responsible for persisting and managing registered Dynamic Period Drivers
/// independently of active chart indicators. Persists to Data/DynamicPeriodDrivers/dynamic_period_drivers.json.
/// </summary>
public class DynamicPeriodDriverService : IDynamicPeriodDriverService, IDisposable
{
    private const string FileName = "dynamic_period_drivers.json";
    private readonly string _filePath;
    private readonly ILogger<DynamicPeriodDriverService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly List<CoreIndicatorSettings> _cache = new();
    private bool _isLoaded;
    private bool _isDisposed;

    public DynamicPeriodDriverService(string? customFilePath = null, ILogger<DynamicPeriodDriverService>? logger = null)
    {
        _logger = logger ?? NullLogger<DynamicPeriodDriverService>.Instance;
        _filePath = customFilePath ?? PathDiscovery.ResolveDynamicPeriodDriversPath(FileName);
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
            _logger.LogError(ex, "Failed to initialize dynamic period drivers cache from disk");
        }
    }

    private string GetFilePath() => _filePath;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoreIndicatorSettings>> GetDynamicPeriodDriversAsync()
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
    public IReadOnlyList<CoreIndicatorSettings> GetDynamicPeriodDrivers()
    {
        lock (_cache)
        {
            return _cache.Select(i => i.Snapshot()).ToList();
        }
    }

    /// <inheritdoc />
    public CoreIndicatorSettings? GetDynamicPeriodDriver(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_cache)
        {
            return _cache.FirstOrDefault(i => i.Id == id)?.Snapshot();
        }
    }

    /// <inheritdoc />
    public async Task SaveDynamicPeriodDriverAsync(CoreIndicatorSettings indicator)
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
    public async Task<bool> DeleteDynamicPeriodDriverAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            int index = _cache.FindIndex(i => i.Id == id);
            if (index < 0) return false;

            _cache.RemoveAt(index);
            await PersistToDiskAsync().ConfigureAwait(false);
            return true;
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

            if (File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                var items = JsonSerializer.Deserialize<List<CoreIndicatorSettings>>(json, _jsonOptions);
                _cache.Clear();
                if (items != null)
                {
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
            _logger.LogError(ex, "Failed to load dynamic period drivers from {Path}", _filePath);
            _isLoaded = true; // prevent repeated failing reads
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task PersistToDiskAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_cache, _jsonOptions);
            var tempFile = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
            File.Move(tempFile, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist dynamic period drivers to {Path}", _filePath);
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
