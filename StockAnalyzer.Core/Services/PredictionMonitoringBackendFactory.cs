using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Selects the monitoring backend ONCE at startup and exposes its single I/O facade (<see cref="Log"/>, <see cref="Health"/>
/// and <see cref="Outbox"/> are the same object per backend). A completed monitoring.db import marker selects SQLite whatever
/// the setting says; the legacy JSON files are never a live fallback afterwards. Without a marker, <c>Backend = Sqlite</c> runs
/// the explicit quiescent import first (cold cutover, before any worker exists) and a failed import throws.
/// </summary>
public sealed class PredictionMonitoringBackendFactory
{
    private readonly Lazy<Task<Resolved>> _resolved;
    private sealed record Resolved(bool UsesSqlite, IPredictionLogService Log, ICurrentPredictionHealthService Health, IMonitoringNotificationOutbox Outbox,
        IAttestedPredictionMonitoring? Attested, IAttestedEvidenceDiagnostics? Diagnostics);

    public PredictionMonitoringBackendFactory(IModelGenerationRegistry registry, IOptions<PredictionMonitoringSettings>? options = null,
        ILogger<PredictionMonitoringBackendFactory>? logger = null, TimeProvider? clock = null, string? root = null)
    {
        var settings = options?.Value ?? new PredictionMonitoringSettings();
        var log = logger ?? NullLogger<PredictionMonitoringBackendFactory>.Instance;
        var time = clock ?? TimeProvider.System;
        var monitoringRoot = root ?? PredictionMonitoringPaths.DefaultRoot();
        // Started once, on a pool thread, by the first InitializeAsync call or the first property read; cached including a failure,
        // so every consumer sees the same explicit outcome.
        _resolved = new Lazy<Task<Resolved>>(() => Task.Run(() => ResolveAsync(registry, settings, log, time, monitoringRoot)));
    }

    /// <summary>
    /// Selects the backend (and runs the one-time JSON import when it is due) without blocking the caller. Awaiting it before the first
    /// consumer lets an application keep its UI responsive during a long import; a failed import faults the task and the same failure is
    /// thrown by every property afterwards (there is no fallback). The token only stops the wait: a started import always finishes, because
    /// a half-done import must not be abandoned. Repeated calls share the one resolution.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) => _resolved.Value.WaitAsync(ct);

    // A consumer that did not initialise first blocks here until the same resolution finishes (it runs on the pool, so this cannot deadlock).
    private Resolved Current => _resolved.Value.GetAwaiter().GetResult();

    public bool UsesSqlite => Current.UsesSqlite;
    public IPredictionLogService Log => Current.Log;
    public ICurrentPredictionHealthService Health => Current.Health;
    public IMonitoringNotificationOutbox Outbox => Current.Outbox;

    /// <summary>Producer-attested evidence capability; null with the JSON backend (the feature then stays dormant).</summary>
    public IAttestedPredictionMonitoring? Attested => Current.Attested;

    /// <summary>Integrity diagnostics of attested evidence; null with the JSON backend.</summary>
    public IAttestedEvidenceDiagnostics? Diagnostics => Current.Diagnostics;

    private static async Task<Resolved> ResolveAsync(IModelGenerationRegistry registry, PredictionMonitoringSettings settings,
        ILogger logger, TimeProvider clock, string root)
    {
        foreach (var warning in PredictionMonitoringSettingsValidator.Validate(settings)) logger.LogWarning("{SettingWarning}", warning);
        var store = new SqliteMonitoringStore(root, settings, logger);
        bool complete = await store.IsImportCompleteAsync(CancellationToken.None).ConfigureAwait(false);
        if (complete && settings.Backend == PredictionMonitoringBackendKind.Json)
            logger.LogWarning("The monitoring database import is complete, so SQLite stays selected although Backend is Json; there is no automatic fallback.");
        if (!complete && settings.Backend == PredictionMonitoringBackendKind.Sqlite)
        {
            logger.LogInformation("Importing the prediction monitoring JSON files into {Database}.", store.DatabasePath);
            var summary = await PredictionMonitoringMigrator.ImportAsync(store, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            if (summary is not null)
                logger.LogInformation("Imported {Predictions} forecasts, {Outcomes} outcome revisions, {Health} health rows and {Alerts} alerts (manifest {Manifest}).",
                    summary.Predictions, summary.Outcomes, summary.Health, summary.Alerts, summary.ManifestDigest);
            complete = true;
        }
        if (complete)
        {
            var sqlite = new SqlitePredictionLogService(registry, store, settings.OutboxPollInterval, clock, settings.AttestedSources, logger);
            return new Resolved(true, sqlite, sqlite, sqlite.Outbox, sqlite, sqlite);
        }
        var json = new PredictionLogService(registry, clock, root, logger);
        if (settings.AttestedSources.Count > 0)
            logger.LogWarning("Attested sources are configured but the monitoring backend is JSON; producer-attested evidence stays dormant until Backend is Sqlite.");
        return new Resolved(false, json, json, new NoMonitoringNotificationOutbox(), null, null);
    }
}
