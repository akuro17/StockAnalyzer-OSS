using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Default <see cref="IExperimentLogService"/>. Writes two UTF-8 JSON files per run into
/// <see cref="PathDiscovery.ResolveExperimentsDirectory"/>, both through the same wire vocabulary
/// (<see cref="TrainingConfigJson.Options"/>: snake_case, indented, nulls omitted) already used
/// for the job-config JSON sent to <c>run_training.py</c>.
/// </summary>
public sealed class ExperimentLogService : IExperimentLogService
{
    private readonly ILogger<ExperimentLogService> _logger;

    public ExperimentLogService(ILogger<ExperimentLogService>? logger = null)
    {
        _logger = logger ?? NullLogger<ExperimentLogService>.Instance;
    }

    public async Task RecordAsync(TrainingJobConfig config, TrainingRunResult result, CancellationToken ct = default)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var runDir = PathDiscovery.ResolveExperimentsDirectory(result.RunId);

        var configPath = Path.Combine(runDir, "config.json");
        var metricsPath = Path.Combine(runDir, "metrics.json");

        // AtomicJsonFile.SaveAsync (not File.WriteAllTextAsync): these are permanent historical
        // run records, not scratch files, so a crash mid-write must not leave a truncated/corrupt
        // JSON file behind (SA_ARCHITECTURE_RULES.md Sec.3 "Atomic Safe Write"). It also writes
        // via JsonSerializer.SerializeAsync, which never emits a UTF-8 BOM, so the same
        // BOM-vs-json.loads fix (see TrainingConfigJson.Utf8NoBom) carries over for free.
        // AtomicJsonFile.SaveAsync has no CancellationToken parameter; check ct explicitly around
        // each write instead of threading it through, to avoid widening that shared helper's
        // signature for this one caller.
        ct.ThrowIfCancellationRequested();
        await AtomicJsonFile.SaveAsync(configPath, config, TrainingConfigJson.Options).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await AtomicJsonFile.SaveAsync(metricsPath, result, TrainingConfigJson.Options).ConfigureAwait(false);

        _logger.LogInformation("ExperimentLogService: recorded run {RunId} under {RunDir}.", result.RunId, runDir);
    }
}
