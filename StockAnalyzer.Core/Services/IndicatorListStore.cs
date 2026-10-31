using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The stored list of registered indicator settings behind <see cref="DynamicPeriodDriverService"/> and <see cref="SourceIndicatorService"/>:
/// one JSON file, an in-memory cache, and one load/save rule for both.
/// </summary>
/// <remarks>
/// Load states: Unloaded (the file could not be read for a transient reason: <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/>) and Loaded. Readers never fail because of Unloaded; a save or delete in
/// Unloaded throws <see cref="IOException"/> and changes neither the cache nor the file, so an unreadable file is never
/// replaced by an almost empty one. A file that is read but cannot be interpreted is copied to
/// <see cref="CorruptedFile.PathFor"/> and the store starts empty. Every access to the cache runs under <c>lock (_cache)</c>.
/// </remarks>
internal sealed class IndicatorListStore : IDisposable
{
    private readonly string _filePath;
    private readonly string _displayName;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly List<CoreIndicatorSettings> _cache = new();
    private bool _isLoaded;
    private Exception? _lastLoadError;
    private bool _isDisposed;

    /// <param name="filePath">The JSON file.</param>
    /// <param name="displayName">Lower-case plural name used in log and exception texts (for example "source indicators").</param>
    /// <param name="logger">The logger of the owning service, so its category is unchanged.</param>
    public IndicatorListStore(string filePath, string displayName, ILogger logger)
    {
        _filePath = filePath;
        _displayName = displayName;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new IndicatorColorJsonConverter() }
        };

        // Eager initial load of cache: the synchronous getters depend on it.
        string? json = null;
        Exception? readError = null;
        try
        {
            if (File.Exists(_filePath)) json = File.ReadAllText(_filePath);
        }
        catch (Exception ex)
        {
            readError = ex;
        }
        ApplyLoad(json, readError);
    }

    public async Task<IReadOnlyList<CoreIndicatorSettings>> GetAllAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_cache)
            {
                return _cache.Select(i => i.Snapshot()).ToList();
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public IReadOnlyList<CoreIndicatorSettings> GetAll()
    {
        lock (_cache)
        {
            return _cache.Select(i => i.Snapshot()).ToList();
        }
    }

    public CoreIndicatorSettings? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_cache)
        {
            return _cache.FirstOrDefault(i => i.Id == id)?.Snapshot();
        }
    }

    public async Task SaveAsync(CoreIndicatorSettings indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);

        await EnsureLoadedForWriteAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var item = indicator.Snapshot();
            if (string.IsNullOrEmpty(item.Id))
            {
                item.Id = Guid.NewGuid().ToString();
            }

            List<CoreIndicatorSettings> snapshot;
            lock (_cache)
            {
                int index = _cache.FindIndex(i => i.Id == item.Id);
                if (index >= 0)
                {
                    _cache[index] = item;
                }
                else
                {
                    _cache.Add(item);
                }
                snapshot = new List<CoreIndicatorSettings>(_cache);
            }

            await PersistToDiskAsync(snapshot).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        await EnsureLoadedForWriteAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            List<CoreIndicatorSettings> snapshot;
            lock (_cache)
            {
                int index = _cache.FindIndex(i => i.Id == id);
                if (index < 0) return false;

                _cache.RemoveAt(index);
                snapshot = new List<CoreIndicatorSettings>(_cache);
            }

            await PersistToDiskAsync(snapshot).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (Volatile.Read(ref _isLoaded)) return;

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _isLoaded)) return;

            string? json = null;
            Exception? readError = null;
            try
            {
                if (File.Exists(_filePath)) json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                readError = ex;
            }
            ApplyLoad(json, readError);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>A save or delete needs the stored data: when it could not be read, nothing is written (failed read is not "no data").</summary>
    private async Task EnsureLoadedForWriteAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (!Volatile.Read(ref _isLoaded))
        {
            throw new IOException($"The {_displayName} at '{_filePath}' could not be read; the change was not saved so that the stored data is kept.", _lastLoadError);
        }
    }

    /// <summary>
    /// Completes one load attempt. <paramref name="json"/> is null when the file does not exist. Returns true when the cache is
    /// authoritative (state Loaded), false when the file must be read again later (state Unloaded).
    /// </summary>
    private bool ApplyLoad(string? json, Exception? readError)
    {
        if (readError is IOException or UnauthorizedAccessException)
        {
            _lastLoadError = readError;
            _logger.LogError(readError, "Failed to read {DisplayName} from {Path}; saving is disabled until the file can be read", _displayName, _filePath);
            return false;
        }

        List<CoreIndicatorSettings> items = new();
        if (readError != null)
        {
            QuarantineFile(readError);
        }
        else if (json != null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<CoreIndicatorSettings>>(json, _jsonOptions);
                if (parsed != null)
                {
                    foreach (var item in parsed)
                    {
                        DefaultCoreIndicatorSettings.AutoHeal(item);
                        items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                items.Clear();
                QuarantineFile(ex);
            }
        }

        lock (_cache)
        {
            _cache.Clear();
            _cache.AddRange(items);
        }
        _lastLoadError = null;
        Volatile.Write(ref _isLoaded, true);
        return true;
    }

    /// <summary>Keeps the unreadable content as <see cref="CorruptedFile.PathFor"/> (overwriting an older copy) before the next save replaces it.</summary>
    private void QuarantineFile(Exception cause)
    {
        string backupPath = CorruptedFile.PathFor(_filePath);
        _logger.LogWarning(cause, "The {DisplayName} file {Path} could not be interpreted; keeping a copy as {Backup} and starting empty", _displayName, _filePath, backupPath);
        try
        {
            if (File.Exists(_filePath)) File.Copy(_filePath, backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep a copy of {Path}", _filePath);
        }
    }

    private async Task PersistToDiskAsync(List<CoreIndicatorSettings> snapshot)
    {
        try
        {
            await AtomicJsonFile.SaveAsync(_filePath, snapshot, _jsonOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {DisplayName} to {Path}", _displayName, _filePath);
            throw;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _semaphore.Dispose();
    }
}
