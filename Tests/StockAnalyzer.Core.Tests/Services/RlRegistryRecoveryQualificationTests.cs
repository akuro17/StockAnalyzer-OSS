#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlRegistryRecoveryQualificationTests
{
    public const string Enabled = "SA_RL_R07_QUALIFICATION";
    public const string ConfigFile = "SA_RL_R07_QUALIFICATION_CONFIG";
    public sealed record RecoveryConfig(string Output, string EarlierDirection, string EarlierActor, string LaterDirection, string LaterActor);
    public sealed class RecoveryQualificationAttribute : FactAttribute
    { public RecoveryQualificationAttribute() { if (Environment.GetEnvironmentVariable(Enabled) != "1") Skip = "Opt-in owned R07 actual qualified T15/T16 backup dependency verification."; } }
    [RecoveryQualification] public async Task ActualActorBackupRechecksItsFrozenDependencyBeforeExplicitRecovery()
    {
        var config = JsonSerializer.Deserialize<RecoveryConfig>(File.ReadAllText(Environment.GetEnvironmentVariable(ConfigFile)!))!;
        string root = Path.Combine(config.Output, "owned_recovery_" + Guid.NewGuid().ToString("N"));
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var earlierDirection = await registry.RegisterRlAsync(config.EarlierDirection);
        var earlier = await registry.RegisterRlAsync(config.EarlierActor); await registry.ActivateRlAsync(earlier.ModelId);
        await registry.RegisterRlAsync(config.LaterDirection);
        var later = await registry.RegisterRlAsync(config.LaterActor); await registry.ActivateRlAsync(later.ModelId);
        string pointer = Path.Combine(root, "Generations", RlFiles.RlFolder, RlContinuousContract.ActiveFile);
        byte[] validBackup = File.ReadAllBytes(pointer + ".bak"); File.WriteAllText(pointer, "{");
        Assert.Null(registry.RlContinuousActiveId);
        var reviewed = registry.PreviewRlRecovery(RlContinuousContract.Target); Assert.Equal(earlier.ModelId, reviewed.BackupModelId);
        using var lease = registry.AcquireRl(earlierDirection.ModelId)!;
        string model = lease.ModelPath; byte[] original = File.ReadAllBytes(model); File.AppendAllText(model, "changed");
        var invalid = registry.PreviewRlRecovery(RlContinuousContract.Target); Assert.Null(invalid.BackupModelId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RecoverRlPointerAsync(reviewed));
        Assert.Equal("{", File.ReadAllText(pointer)); Assert.Equal(validBackup, File.ReadAllBytes(pointer + ".bak"));
        File.WriteAllBytes(model, original);
        Assert.True(await registry.RecoverRlPointerAsync(registry.PreviewRlRecovery(RlContinuousContract.Target)));
        Assert.Equal(earlier.ModelId, registry.RlContinuousActiveId); Assert.Null(registry.RlActiveId);
        Assert.Equal(validBackup, File.ReadAllBytes(pointer + ".bak"));
        using var reopened = new ModelGenerationRegistry(Path.Combine(root, "Generations")); Assert.Equal(earlier.ModelId, reopened.RlContinuousActiveId);
        await File.WriteAllTextAsync(Path.Combine(config.Output, "actual_recovery_evidence.json"), JsonSerializer.Serialize(new {
            root, earlier_actor = earlier.ModelId, later_actor = later.ModelId, frozen_dependency = earlierDirection.ModelId,
            corrupt_dependency_rejected = true, original_backup_preserved = true, explicit_recovery = true, original_active_untouched = true }, RlFiles.Json));
    }
}
