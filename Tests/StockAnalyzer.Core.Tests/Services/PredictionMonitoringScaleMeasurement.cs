#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// FP04 opt-in measurement. Runs only when SA_FP04_MEASURE_OUT names an output file (otherwise it returns at once), so the
/// normal suite never pays for it. It builds P forecast audits with an audit history and a B-bar observation, runs the
/// legacy refresh and appends timings, allocations, the environment and a semantic fingerprint of every committed outcome
/// and health row. The fingerprint must be identical before and after an optimization.
/// </summary>
public sealed class PredictionMonitoringScaleMeasurement : IDisposable
{
    private const int Predictions = 5_000, Bars = 5_000, History = 4, Horizon = 5, ModelCount = 50, TargetCount = 40, Repeats = 3;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_fp04_scale_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Hex(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    [EnvironmentFact("SA_FP04_MEASURE_OUT")]
    public async Task MeasureLegacyRefreshAtScale()
    {
        var output = Environment.GetEnvironmentVariable("SA_FP04_MEASURE_OUT")!;
        var label = Environment.GetEnvironmentVariable("SA_FP04_MEASURE_LABEL") ?? "unlabelled";
        var monitorRoot = Path.Combine(_root, "monitor"); Directory.CreateDirectory(Path.Combine(monitorRoot, "predictions"));
        var start = Now.UtcDateTime.Date.AddDays(-Bars);
        var candles = Enumerable.Range(0, Bars).Select(i => new CandleData(start.AddDays(i), 100m + i % 7, 101m + i % 7, 99m + i % 7, 100m + i % 7, 1000))
            .ToImmutableArray();
        for (int j = 0; j < Predictions; j++)
        {
            var id = Hex("prediction" + j);
            var anchor = PredictionLogService.Utc(candles[(j * 7) % (Bars - Horizon - 1)].Timestamp);
            var prediction = new MonitoredPrediction(id, Hex("model" + j % ModelCount), Hex("hash"), "S", TimeframeType.Daily, anchor, Horizon,
                .005m, "Up", Hex("target" + j / ModelCount % TargetCount), Hex("feature"), Hex("input" + j), anchor);
            var outcomes = Enumerable.Range(0, History).Select(r => new PredictionOutcomeRevision(r, Hex("rev" + j + ":" + r),
                PredictionOutcomeState.Pending, anchor.AddMinutes(r + 1))).ToImmutableArray();
            await AtomicJsonFile.SaveAsync(Path.Combine(monitorRoot, "predictions", id + ".json"),
                new PredictionAuditRecord(PredictionMonitoringPolicy.SchemaVersion, prediction, outcomes), TrainingConfigJson.Options);
        }
        var monitor = new PredictionLogService(new Mock<IModelGenerationRegistry>().Object, new RetrainingSchedulerTests.MutableClock(Now), monitorRoot);
        var observation = new MonitoringObservation(PredictionLogService.SourceRevision(candles), Now, candles,
            Enumerable.Repeat(true, Bars).ToImmutableArray());

        async Task<(double Ms, double Mb)> Run()
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            await monitor.RefreshAsync("S", TimeframeType.Daily, observation);
            watch.Stop();
            return (watch.Elapsed.TotalMilliseconds, (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0);
        }
        var first = await Run(); // matures and writes every audit
        var steady = new List<(double Ms, double Mb)>();
        for (int i = 0; i < Repeats; i++) steady.Add(await Run()); // same evidence again: read, search, hash, evaluate, no audit writes

        var fingerprint = await Fingerprint(monitorRoot);
        var groups = JsonSerializer.Deserialize<List<ModelMonitoringSnapshot>>(File.ReadAllText(Path.Combine(monitorRoot, "health.json")), TrainingConfigJson.Options)!.Count;
        var ms = steady.Select(s => s.Ms).OrderBy(v => v).ToArray();
        File.AppendAllText(output, string.Join(Environment.NewLine,
            $"[{label}] P={Predictions} B={Bars} history={History} groups={groups}",
            $"  env: {RuntimeInformation.OSDescription} | {RuntimeInformation.FrameworkDescription} | cpus={Environment.ProcessorCount} | {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}",
            $"  first refresh (matures {Predictions}): {first.Ms:F0} ms, {first.Mb:F0} MB allocated",
            $"  steady refresh x{Repeats}: min {ms[0]:F0} ms, median {ms[ms.Length / 2]:F0} ms, max {ms[^1]:F0} ms; allocated {steady.Min(s => s.Mb):F0}-{steady.Max(s => s.Mb):F0} MB",
            $"  fingerprint: {fingerprint}") + Environment.NewLine);
    }

    /// <summary>Digest of every outcome revision and every health row (without the evaluation instant and the random alert ids).</summary>
    private static async Task<string> Fingerprint(string monitorRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(monitorRoot, "predictions"), "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var record = (await AtomicJsonFile.LoadAsync<PredictionAuditRecord>(path, TrainingConfigJson.Options))!;
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + JsonSerializer.Serialize(record.Outcomes, TrainingConfigJson.Options)));
        }
        var health = JsonSerializer.Deserialize<List<ModelMonitoringSnapshot>>(File.ReadAllText(Path.Combine(monitorRoot, "health.json")), TrainingConfigJson.Options)!;
        foreach (var s in health.OrderBy(s => s.ModelId, StringComparer.Ordinal).ThenBy(s => s.TargetContractHash, StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s with { EvaluatedUtc = default, LastAlertId = null }, TrainingConfigJson.Options)));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
