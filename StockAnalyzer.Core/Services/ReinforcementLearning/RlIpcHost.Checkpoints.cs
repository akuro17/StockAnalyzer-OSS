#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

internal sealed partial class RlIpcHost
{
    internal RlCheckpointStore? Checkpoints { get; set; }
    private sealed record SavedSession(string Id, string Block, string Role, int Seed, int Checkpoint,
        float[][] Actions, string SnapshotIdentity);
    private sealed record SavedReport(string Role, int Seed, int Checkpoint, RlEpisodeEvaluation[] Rows);
    private sealed record CoreCheckpoint(RlWorkEvidence Work, SavedSession[] Sessions, SavedReport[] Reports,
        Dictionary<int, int> TrainingTransitions, RlUpdateDiagnostics[] Updates, RlActionDiagnostics[] Actions,
        RlInformationUsage[] Information, int? SelectedSeed, int? SelectedStep, Dictionary<string, JsonElement> Baselines,
        RlFinancialAccount? LastAccount);
    private object PrepareCheckpoint(JsonElement payload)
    {
        int seed = payload.GetProperty("seed").GetInt32(), steps = payload.GetProperty("steps").GetInt32();
        string phase = payload.GetProperty("phase").GetString()!;
        if (_trainingTransitions.GetValueOrDefault(seed) != steps || _updates.LastOrDefault(u => u.Seed == seed)?.CheckpointStep != steps
            || _sessions.Values.Any(s => s.Role != "train") || (phase == "train" ? Selected is not null : Selected is null))
            throw new InvalidDataException("Checkpoint does not describe a coherent optimizer-complete phase.");
        int interval = _source.Catalog.Oos.IsEmpty ? RlContract.SmokeCheckpointInterval : RlContinuousContract.ValidationCheckpointInterval;
        if (phase == "train" && steps % interval == 0
            && _reports.GetValueOrDefault(("validation", seed, steps))?.Count != _source.Catalog.Validation.Length)
            throw new InvalidDataException("Scheduled checkpoint validation is incomplete.");
        return Checkpoints!.Prepare(CaptureCheckpoint(), seed, steps, phase);
    }
    private CoreCheckpoint CaptureCheckpoint() => new(_work.Snapshot(),
        _sessions.Select(p => new SavedSession(p.Key, p.Value.Block.Id, p.Value.Role, p.Value.Seed, p.Value.Checkpoint,
            p.Value.RawActions.ToArray(), ContentHash.OfText(JsonSerializer.Serialize(p.Value.Snapshot, RlFiles.Json)))).ToArray(),
        _reports.Select(p => new SavedReport(p.Key.Role, p.Key.Seed, p.Key.Checkpoint, p.Value.ToArray())).ToArray(),
        new(_trainingTransitions), _updates.ToArray(),
        _actionDiagnostics.Select(p => new RlActionDiagnostics(p.Key.Role, p.Key.Seed, p.Key.Checkpoint, p.Value.All.Snapshot(), p.Value.Entry.Snapshot())).ToArray(),
        _informationUsage.Values.ToArray(), Selected?.Seed, Selected?.Checkpoint,
        Baselines.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value, RlFiles.Json)), _lastSuccessfulAccount);
    private object RestoreCheckpoint(CancellationToken ct)
    {
        if (_scaler is null || _sessions.Count != 0 || _updates.Count != 0 || Checkpoints!.ResumePath is null)
            throw new InvalidOperationException("Checkpoint restore requires a fresh ready host.");
        var saved = RlCheckpointStore.Read<CoreCheckpoint>(Path.Combine(Checkpoints.ResumePath, RlCheckpointContract.CoreState));
        _work.Restore(saved.Work, Checkpoints.Used.Transitions, Checkpoints.Used.Starts);
        var identities = new Dictionary<string, string>();
        // Reconstruct real decimal Core state by bounded active-episode raw-action replay. Replay work is charged.
        foreach (var session in saved.Sessions)
        {
            if (session.Actions.Length > RlContract.EpisodeHorizon) throw new InvalidDataException("Saved episode replay exceeds the horizon.");
            string id = Guid.NewGuid().ToString("N"); identities.Add(session.Id, id);
            Reset(id, JsonSerializer.SerializeToElement(new { block_id = session.Block, role = session.Role, seed = session.Seed, checkpoint = session.Checkpoint }), ct);
            foreach (float[] actions in session.Actions) Step(id, JsonSerializer.SerializeToElement(new { actions }), ct);
            if (ContentHash.OfText(JsonSerializer.Serialize(_sessions[id].Snapshot, RlFiles.Json)) != session.SnapshotIdentity)
                throw new InvalidDataException("Unavailable: replayed Core account state differs from the committed checkpoint.");
        }
        _reports.Clear(); foreach (var row in saved.Reports) _reports.Add((row.Role, row.Seed, row.Checkpoint), row.Rows.ToList());
        _trainingTransitions.Clear(); foreach (var p in saved.TrainingTransitions) _trainingTransitions.Add(p.Key, p.Value);
        _updates.Clear(); _updates.AddRange(saved.Updates);
        _actionDiagnostics.Clear(); foreach (var row in saved.Actions)
            _actionDiagnostics.Add((row.Role, row.Seed, row.CheckpointStep), (new(row.AllActions), new(row.EntryActions)));
        _informationUsage.Clear(); foreach (var row in saved.Information) _informationUsage.Add((row.Role, row.Seed, row.Checkpoint, row.BlockId), row);
        Selected = saved.SelectedSeed is { } seed && saved.SelectedStep is { } step ? (seed, step) : null;
        Baselines.Clear(); foreach (var p in saved.Baselines) Baselines.Add(p.Key, p.Value);
        _lastSuccessfulAccount = saved.LastAccount;
        return new { sessions = identities, restored = true };
    }
}
