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
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private readonly ILogger<ExperimentLogService> _logger;

    public ExperimentLogService(ILogger<ExperimentLogService>? logger = null)
    {
        _logger = logger ?? NullLogger<ExperimentLogService>.Instance;
    }

    /// <summary>
    /// Thread-safe append method for experiment logs with retry on transient file sharing violations.
    /// </summary>
    public static async Task AppendAsync(string filePath, string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (attempt < maxRetries)
                {
                    await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            WriteLock.Release();
        }
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
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await AtomicJsonFile.SaveAsync(configPath, config, TrainingConfigJson.Options).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await AtomicJsonFile.SaveAsync(metricsPath, result, TrainingConfigJson.Options).ConfigureAwait(false);
        }
        finally
        {
            WriteLock.Release();
        }

        _logger.LogInformation("ExperimentLogService: recorded run {RunId} under {RunDir}.", result.RunId, runDir);
    }
}
