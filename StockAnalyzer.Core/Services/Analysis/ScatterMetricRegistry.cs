using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Thread-safe store of the confirmed scatter metrics, persisted to <c>Data\Config\scatter_metrics.json</c> with <see cref="AtomicJsonFile"/>.
/// The first read starts in the constructor and is awaited through <see cref="Loaded"/>; every change waits for it, so a write can never overwrite a
/// list that has not been read yet.
/// </summary>
public sealed class ScatterMetricRegistry : IScatterMetricRegistry
{
    public const string FileName = "scatter_metrics.json";

    private readonly string _filePath;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly ILogger<ScatterMetricRegistry> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Replaced as a whole, never modified in place, so Snapshot() needs no lock.
    private volatile IReadOnlyList<ScatterMetricDefinition> _items = Array.Empty<ScatterMetricDefinition>();
    private int _loadDroppedCount;

    public ScatterMetricRegistry(
        IStockAnalyzerSettings settings,
        IIndicatorFactory? indicatorFactory = null,
        string? customFilePath = null,
        ILogger<ScatterMetricRegistry>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
        _logger = logger ?? NullLogger<ScatterMetricRegistry>.Instance;
        _filePath = customFilePath ?? PathDiscovery.ResolveConfigPath(FileName);

        Loaded = Task.Run(LoadInitialAsync);
    }

    public Task Loaded { get; }

    public event EventHandler? Changed;

    public IReadOnlyList<ScatterMetricDefinition> Snapshot() => _items.Select(item => item.Clone()).ToList();

    public int TakeLoadDroppedCount() => Interlocked.Exchange(ref _loadDroppedCount, 0);

    public async Task<ScatterMetricRegistryResult> ReplaceAllAsync(IReadOnlyList<ScatterMetricDefinition> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        await Loaded.ConfigureAwait(false);

        if (metrics.Count > _settings.ScatterMaxRegisteredMetrics)
        {
            return ScatterMetricRegistryResult.LimitReached;
        }

        var ids = new HashSet<Guid>();
        foreach (ScatterMetricDefinition metric in metrics)
        {
            if (ScatterMetricRules.Validate(metric, _settings.ScatterMaxMetricOffset, _indicatorFactory) != ScatterMetricInvalidReason.None
                || !ids.Add(metric.Id))
            {
                return ScatterMetricRegistryResult.Invalid;
            }
        }

        List<ScatterMetricDefinition> next = metrics.Select(metric => metric.Clone()).ToList();
        bool changed;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            changed = ScatterMetricRules.DiffersInIdOrContent(_items, next);
            if (!changed)
            {
                return ScatterMetricRegistryResult.Ok;
            }

            try
            {
                await AtomicJsonFile.SaveAsync(_filePath, new ScatterMetricFile { Metrics = next }, _jsonOptions).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to save the registered scatter metrics to {Path}", _filePath);
                return ScatterMetricRegistryResult.PersistFailed;
            }

            _items = next;
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return ScatterMetricRegistryResult.Ok;
    }

    private async Task LoadInitialAsync()
    {
        bool loadedAny = false;
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            ScatterMetricFile? file = await AtomicJsonFile.LoadAsync<ScatterMetricFile>(_filePath, _jsonOptions).ConfigureAwait(false);
            if (file is null || file.Metrics is null)
            {
                _logger.LogError("The scatter metric file {Path} is empty or not a metric list; it is ignored and left unchanged.", _filePath);
                return;
            }

            if (file.FormatVersion > ScatterMetricFile.CurrentFormatVersion)
            {
                _logger.LogError(
                    "The scatter metric file {Path} has format version {Version}; this build reads up to {Supported}. It is ignored and left unchanged.",
                    _filePath, file.FormatVersion, ScatterMetricFile.CurrentFormatVersion);
                return;
            }

            var accepted = new List<ScatterMetricDefinition>();
            var ids = new HashSet<Guid>();
            int dropped = 0;
            int limit = _settings.ScatterMaxRegisteredMetrics;

            foreach (ScatterMetricDefinition? entry in file.Metrics)
            {
                ScatterMetricInvalidReason reason = ScatterMetricRules.Validate(entry, _settings.ScatterMaxMetricOffset, _indicatorFactory);
                if (reason != ScatterMetricInvalidReason.None)
                {
                    dropped++;
                    _logger.LogWarning("A saved scatter metric ({Id}) was dropped: {Reason}.", entry?.Id, reason);
                }
                else if (!ids.Add(entry!.Id))
                {
                    dropped++;
                    _logger.LogWarning("A saved scatter metric ({Id}) was dropped: its Id is used twice.", entry.Id);
                }
                else if (accepted.Count >= limit)
                {
                    dropped++;
                    _logger.LogWarning("A saved scatter metric ({Id}) was dropped: more than {Limit} metrics are saved.", entry.Id, limit);
                }
                else
                {
                    ScatterMetricParameterNormalizer.Normalize(entry!, _indicatorFactory);
                    accepted.Add(entry!);
                }
            }

            _items = accepted;
            Interlocked.Exchange(ref _loadDroppedCount, dropped);
            loadedAny = accepted.Count > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to read the registered scatter metrics from {Path}; the list starts empty and the file is left unchanged.", _filePath);
            _items = Array.Empty<ScatterMetricDefinition>();
        }

        if (loadedAny)
        {
            RaiseChanged();
        }
    }

    /// <summary>Calls every handler; a handler that throws is logged and never prevents the others, nor faults <see cref="Loaded"/> or a committed change.</summary>
    private void RaiseChanged()
    {
        Delegate[]? handlers = Changed?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers)
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "A handler of the scatter metric registry's Changed event threw.");
            }
        }
    }
}
