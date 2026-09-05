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
/// Service responsible for managing, persisting, and resetting user-customized indicator defaults.
/// Persists defaults to Data/IndicatorDefaults/user_indicator_defaults.json.
/// </summary>
public class IndicatorUserDefaultService : IIndicatorUserDefaultService, IDisposable
{
    private const string DefaultsFileName = "user_indicator_defaults.json";
    private readonly ILogger<IndicatorUserDefaultService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _isDisposed;

    public IndicatorUserDefaultService() : this(null)
    {
    }

    public IndicatorUserDefaultService(ILogger<IndicatorUserDefaultService>? logger)
    {
        _logger = logger ?? NullLogger<IndicatorUserDefaultService>.Instance;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new IndicatorColorJsonConverter() }
        };
    }

    private string GetFilePath() => PathDiscovery.ResolveIndicatorDefaultsPath(DefaultsFileName);

    /// <inheritdoc />
    public async Task<Dictionary<IndicatorType, CoreIndicatorSettings>> LoadUserDefaultsAsync()
    {
        var filePath = GetFilePath();
        if (!File.Exists(filePath))
        {
            return new Dictionary<IndicatorType, CoreIndicatorSettings>();
        }

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var list = await JsonSerializer.DeserializeAsync<List<CoreIndicatorSettings>>(stream, _jsonOptions).ConfigureAwait(false);
            if (list == null) return new Dictionary<IndicatorType, CoreIndicatorSettings>();

            var result = new Dictionary<IndicatorType, CoreIndicatorSettings>();
            foreach (var item in list)
            {
                if (item.TypeEnum.HasValue)
                {
                    DefaultCoreIndicatorSettings.AutoHeal(item);
                    result[item.TypeEnum.Value] = item;
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load indicator user defaults from {FilePath}", filePath);
            return new Dictionary<IndicatorType, CoreIndicatorSettings>();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public Dictionary<IndicatorType, CoreIndicatorSettings> LoadUserDefaults()
    {
        var filePath = GetFilePath();
        if (!File.Exists(filePath))
        {
            return new Dictionary<IndicatorType, CoreIndicatorSettings>();
        }

        _semaphore.Wait();
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var list = JsonSerializer.Deserialize<List<CoreIndicatorSettings>>(stream, _jsonOptions);
            if (list == null) return new Dictionary<IndicatorType, CoreIndicatorSettings>();

            var result = new Dictionary<IndicatorType, CoreIndicatorSettings>();
            foreach (var item in list)
            {
                if (item.TypeEnum.HasValue)
                {
                    DefaultCoreIndicatorSettings.AutoHeal(item);
                    result[item.TypeEnum.Value] = item;
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load indicator user defaults from {FilePath}", filePath);
            return new Dictionary<IndicatorType, CoreIndicatorSettings>();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveUserDefaultAsync(CoreIndicatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.TypeEnum.HasValue) return;

        var defaults = await LoadUserDefaultsAsync().ConfigureAwait(false);
        var clone = settings.Clone();
        clone.IsEnabled = true;
        defaults[settings.TypeEnum.Value] = clone;

        await WriteUserDefaultsAsync(defaults.Values.ToList()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResetToSystemDefaultAsync(IndicatorType type)
    {
        var defaults = await LoadUserDefaultsAsync().ConfigureAwait(false);
        if (defaults.Remove(type))
        {
            await WriteUserDefaultsAsync(defaults.Values.ToList()).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ResetAllToSystemDefaultAsync()
    {
        var filePath = GetFilePath();
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete indicator user defaults file at {FilePath}", filePath);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task WriteUserDefaultsAsync(List<CoreIndicatorSettings> list)
    {
        var filePath = GetFilePath();
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var tempPath = $"{filePath}.tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, list, _jsonOptions).ConfigureAwait(false);
            }

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write indicator user defaults to {FilePath}", filePath);
        }
        finally
        {
            _semaphore.Release();
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
