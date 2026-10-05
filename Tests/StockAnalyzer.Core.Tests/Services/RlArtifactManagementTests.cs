#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed partial class RlGenerationAndQValueTests
{
    private RlArtifactSettings ArtifactSettings => new(Path.Combine(_root, "config", RlArtifactSettings.FileName));
    private static readonly RlArtifactPolicy CompleteArtifactPolicy = new(NonactiveRetentionDays: 0, NonactiveKeepCount: 0);
    private ModelGenerationRegistry ArtifactRegistry(Func<DateTimeOffset>? clock = null, Func<string,long>? free = null, Action<string>? delete = null)
    {
        var registry = new ModelGenerationRegistry(RegistryRoot) {
            ArtifactClock = clock ?? (() => DateTimeOffset.UtcNow),
            ArtifactFreeSpace = free ?? (_ => long.MaxValue),
            DeleteDirectory = delete ?? (path => Directory.Delete(path, true)),
        };
        registry.ConfigureRlArtifacts(_root, ArtifactSettings); return registry;
    }
    [Fact] public async Task R08_DefaultsSavedSnapshotSchemaAndRequiredRetention()
    {
        var settings = ArtifactSettings;
        Assert.Equal(10m, settings.Snapshot.QuotaGiB); Assert.Equal(5m, settings.Snapshot.MinimumFreeGiB);
        Assert.Equal(7, settings.Snapshot.FailedRetentionDays); Assert.False(settings.Snapshot.IsComplete);
        using var registry = ArtifactRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.BeginRlArtifactRunAsync(Guid.NewGuid(), _root));
        Assert.False(Directory.Exists(Path.Combine(_root, RlFiles.TrainingFolder, RlFiles.RlFolder)));
        var policy = CompleteArtifactPolicy with { QuotaGiB = 2m, MinimumFreeGiB = 1m, FailedRetentionDays = 9 };
        await registry.SaveRlArtifactPolicyAsync(policy); Assert.Equal(policy, new RlArtifactSettings(Path.Combine(_root,"config",RlArtifactSettings.FileName)).Snapshot);
        Assert.Throws<ArgumentException>(() => (policy with { NonactiveKeepCount = -1 }).Validate());
        Assert.Throws<ArgumentException>(() => (policy with { QuotaGiB = 0 }).Validate());
        Assert.Throws<OverflowException>(() => (policy with { QuotaGiB = decimal.MaxValue }).Validate());
        File.WriteAllText(Path.Combine(_root,"config",RlArtifactSettings.FileName), "{\"schemaVersion\":999,\"policy\":{}}");
        Assert.Throws<InvalidDataException>(() => settings.Snapshot);
    }
    [Fact] public async Task R08_AdmissionChecksQuotaAndFreeSpaceBoundariesAndFreezesPolicy()
    {
        long free = 5 * RlArtifactSettings.BytesPerGiB;
        using var registry = ArtifactRegistry(free: _ => free);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        using var first = await registry.BeginRlArtifactRunAsync(Guid.NewGuid(), _root);
        Assert.Equal(CompleteArtifactPolicy, first!.Policy);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy with { FailedRetentionDays = 8 });
        Assert.Equal(7, first.Policy.FailedRetentionDays);
        free--;
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.BeginRlArtifactRunAsync(Guid.NewGuid(), _root));
        free = long.MaxValue;
        var used = registry.PreviewRlArtifacts()!.UsedBytes;
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy with { QuotaGiB = used / (decimal)RlArtifactSettings.BytesPerGiB });
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.BeginRlArtifactRunAsync(Guid.NewGuid(), _root));
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy with { QuotaGiB = (used + 1m) / RlArtifactSettings.BytesPerGiB });
        using var next = await registry.BeginRlArtifactRunAsync(Guid.NewGuid(), _root); Assert.NotNull(next);
    }
    [Fact] public async Task R08_RunningFailedAndUnknownRunsRemainProtectedUntilEligible()
    {
        var now = DateTimeOffset.UtcNow;
        using var registry = ArtifactRegistry(() => now);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        Guid id = Guid.NewGuid(); var run = await registry.BeginRlArtifactRunAsync(id, _root);
        File.WriteAllText(Path.Combine(run!.Directory, "worker.log"), "owned output");
        Assert.Contains("Lease", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == id.ToString("N")).Reasons);
        await run.FinishAsync(RlArtifactRunState.Failed); now += TimeSpan.FromDays(8);
        Assert.False(registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == id.ToString("N")).Candidate);
        run.Dispose();
        var review = registry.PreviewRlArtifacts()!; Assert.True(review.Entries.Single().Candidate);
        var result = await registry.CleanupRlArtifactsAsync(review); Assert.Single(result.Deleted); Assert.False(Directory.Exists(run.Directory));
        string unknown = Path.Combine(_root, RlFiles.TrainingFolder, RlFiles.RlFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown,"job.json"), "{}");
        Assert.Contains("UnknownOwner", registry.PreviewRlArtifacts()!.Entries.Single().Reasons);
        using var interrupted = await registry.BeginRlArtifactRunAsync(Guid.NewGuid(), _root); string directory = interrupted!.Directory;
        interrupted.Dispose(); now += TimeSpan.FromDays(30);
        Assert.Contains("Interrupted", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Path == directory).Reasons);
    }
    [Fact] public async Task R08_ExpiryAndLatestCountAreBothRequiredAndActivationNeverAutomaticallyPrunes()
    {
        var now = DateTimeOffset.UtcNow;
        using var registry = ArtifactRegistry(() => now);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy with { NonactiveRetentionDays = 1, NonactiveKeepCount = 1 });
        var first = await registry.RegisterRlAsync(Candidate(.01m).Directory);
        var second = await registry.RegisterRlAsync(Candidate(.02m).Directory);
        var third = await registry.RegisterRlAsync(Candidate(.03m).Directory);
        Assert.DoesNotContain(registry.PreviewRlArtifacts()!.Entries, e => e.Candidate);
        now += TimeSpan.FromDays(2);
        var review = registry.PreviewRlArtifacts()!;
        Assert.Contains(review.Entries, e => e.Id == first.ModelId && e.Candidate);
        Assert.Contains("Newest", review.Entries.Single(e => e.Id == third.ModelId).Reasons);
        await registry.ActivateRlAsync(third.ModelId);
        Assert.NotNull(registry.GetRlGeneration(first.ModelId)); Assert.NotNull(registry.GetRlGeneration(second.ModelId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CleanupRlArtifactsAsync(review));
        Assert.Contains("Active", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == third.ModelId).Reasons);
    }
    [Fact] public async Task R08_ChangedLeasePolicyAndCandidateRejectStaleReview()
    {
        var now = DateTimeOffset.UtcNow.AddDays(2);
        using var registry = ArtifactRegistry(() => now);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        var generation = await registry.RegisterRlAsync(Candidate(.01m).Directory);
        var review = registry.PreviewRlArtifacts()!; Assert.True(review.Entries.Single().Candidate);
        using (var lease = registry.AcquireRl(generation.ModelId))
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CleanupRlArtifactsAsync(review));
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy with { NonactiveKeepCount = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CleanupRlArtifactsAsync(review));
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        review = registry.PreviewRlArtifacts()!;
        File.AppendAllText(Path.Combine(RegistryRoot,RlFiles.RlFolder,generation.ModelId,RlFiles.Model), "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CleanupRlArtifactsAsync(review));
        Assert.True(Directory.Exists(review.Entries.Single().Path));
    }
    [Fact] public async Task R08_BackupRollbackAndCorruptPointerProtectionOverrideExpiry()
    {
        var now = DateTimeOffset.UtcNow.AddDays(10);
        using var registry = ArtifactRegistry(() => now);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        var first = await registry.RegisterRlAsync(Candidate(.01m).Directory); await registry.ActivateRlAsync(first.ModelId);
        var second = await registry.RegisterRlAsync(Candidate(.02m).Directory); await registry.ActivateRlAsync(second.ModelId);
        var preview = registry.PreviewRlArtifacts()!;
        var prior = preview.Entries.Single(e => e.Id == first.ModelId);
        Assert.Contains("Rollback",prior.Reasons); Assert.Contains("Backup",prior.Reasons); Assert.False(prior.Candidate);
        File.WriteAllText(Path.Combine(RegistryRoot,RlFiles.RlFolder,RlFiles.Active), "{");
        Assert.DoesNotContain(registry.PreviewRlArtifacts()!.Entries, e => e.Candidate);
    }
    [Fact] public async Task R08_PartialDeletionRetiresCatalogCountsRemainingBytesAndRetries()
    {
        var now = DateTimeOffset.UtcNow.AddDays(2); bool fail = true;
        using var registry = ArtifactRegistry(() => now, delete: path => {
            if (fail) { fail = false; File.Delete(Directory.EnumerateFiles(path).First()); throw new IOException("owned partial deletion failure"); }
            Directory.Delete(path,true);
        });
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        var generation = await registry.RegisterRlAsync(Candidate(.01m).Directory);
        long before = registry.PreviewRlArtifacts()!.UsedBytes;
        var failed = await registry.CleanupRlArtifactsAsync(registry.PreviewRlArtifacts()!);
        Assert.Single(failed.Failures); Assert.Empty(failed.Deleted); Assert.Null(registry.GetRlGeneration(generation.ModelId));
        var pending = registry.PreviewRlArtifacts()!; Assert.Equal(failed.RemainingBytes,pending.UsedBytes);
        Assert.Contains(pending.Entries,e=>e.Kind==RlArtifactKind.PendingDeletion.ToString() && e.Candidate);
        using var reopened = ArtifactRegistry(() => now);
        var result = await reopened.CleanupRlArtifactsAsync(reopened.PreviewRlArtifacts()!);
        Assert.Single(result.Deleted); Assert.Empty(result.Failures); Assert.True(result.RemainingBytes < before);
    }
    [Fact] public async Task R08_ExplicitRootsAndCancellationCannotBroadenDeletion()
    {
        using var registry = ArtifactRegistry();
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.BeginRlArtifactRunAsync(Guid.NewGuid(), Path.Combine(_root,"other")));
        var generation = await registry.RegisterRlAsync(Candidate(.01m).Directory);
        var review = registry.PreviewRlArtifacts()!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.CleanupRlArtifactsAsync(review,cancellation.Token));
        Assert.NotNull(registry.GetRlGeneration(generation.ModelId));
        Assert.Throws<InvalidDataException>(() => {
            using var overlapping = new ModelGenerationRegistry(RegistryRoot);
            overlapping.ConfigureRlArtifacts(Path.Combine(RegistryRoot,"inside"),ArtifactSettings);
        });
    }
    [Fact] public async Task R08_CrossProcessRunAndGenerationLeasesProtectManualCleanup()
    {
        var now = DateTimeOffset.UtcNow;
        using var registry = ArtifactRegistry(() => now);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy with { FailedRetentionDays = 0 });
        string ready=Path.Combine(_root,"ready"), trigger=Path.Combine(_root,"trigger"); Guid runId=Guid.NewGuid();
        var worker=StartWorker("artifact-run",runId.ToString("N"),ready,trigger);
        try
        {
            await WaitFile(ready);
            Assert.Contains("Lease",registry.PreviewRlArtifacts()!.Entries.Single().Reasons);
            File.WriteAllText(trigger,"finish"); await EndWorker(worker);
        }
        catch { StopWorker(worker); throw; }
        now=DateTimeOffset.UtcNow.AddDays(2);
        Assert.True(registry.PreviewRlArtifacts()!.Entries.Single().Candidate);
        var generation=await registry.RegisterRlAsync(Candidate(.01m).Directory);
        var reviewed=registry.PreviewRlArtifacts()!;
        File.Delete(ready); File.Delete(trigger); worker=StartWorker("lease",generation.ModelId,ready,trigger);
        try
        {
            await WaitFile(ready);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>registry.CleanupRlArtifactsAsync(reviewed));
            Assert.NotNull(registry.GetRlGeneration(generation.ModelId));
            File.WriteAllText(trigger,"release"); await EndWorker(worker);
        }
        catch { StopWorker(worker); throw; }
        var result=await registry.CleanupRlArtifactsAsync(registry.PreviewRlArtifacts()!);
        Assert.Equal(2,result.Deleted.Length);
    }
    [Fact] public void R08_OtherVolumeIsRejectedBeforeCreatingArtifacts()
    {
        string? volume=DriveInfo.GetDrives().Where(d=>d.IsReady && !string.Equals(d.Name,Path.GetPathRoot(_root),StringComparison.OrdinalIgnoreCase)).Select(d=>d.Name).FirstOrDefault();
        if (volume is null) return;
        using var registry=new ModelGenerationRegistry(RegistryRoot);
        Assert.Throws<InvalidDataException>(()=>registry.ConfigureRlArtifacts(Path.Combine(volume,"sa_owned_volume_probe_"+Guid.NewGuid().ToString("N")),ArtifactSettings));
    }
    [Fact] public async Task R08_LinkIntroducedAfterReviewCannotExposeOutsideFilesToCleanup()
    {
        var now=DateTimeOffset.UtcNow;
        using var registry=ArtifactRegistry(()=>now);
        await registry.SaveRlArtifactPolicyAsync(CompleteArtifactPolicy);
        var run=await registry.BeginRlArtifactRunAsync(Guid.NewGuid(),_root);
        await run!.FinishAsync(RlArtifactRunState.Failed); run.Dispose(); now+=TimeSpan.FromDays(8);
        var review=registry.PreviewRlArtifacts()!;
        string outside=Path.Combine(_root,"outside"), link=Path.Combine(run.Directory,"link");
        Directory.CreateDirectory(outside); string sentinel=Path.Combine(outside,"sentinel"); File.WriteAllText(sentinel,"preserve");
        if(OperatingSystem.IsWindows())
        {
            var start=new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"' | Out-Null");
            using var process=System.Diagnostics.Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode==0,await stdout+await stderr);
        }
        else Directory.CreateSymbolicLink(link,outside);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(()=>registry.CleanupRlArtifactsAsync(review));
            Assert.Equal("preserve",File.ReadAllText(sentinel)); Assert.True(Directory.Exists(run.Directory));
        }
        finally { Directory.Delete(link); }
    }
}
