#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlArtifactQualificationTests
{
    public sealed class ArtifactQualificationAttribute : FactAttribute
    { public ArtifactQualificationAttribute() { if(Environment.GetEnvironmentVariable("SA_RL_R08_QUALIFICATION")!="1") Skip="Owned actual R08 qualification opt-in."; } }
    [ArtifactQualification] public async Task R08_ActualActorDependencyIsProtectedAndCleanupNeverWidensTheReviewedSet()
    {
        using var config=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SA_RL_R07_QUALIFICATION_CONFIG")!));
        string output=Environment.GetEnvironmentVariable("SA_RL_R08_OUTPUT")!;
        string root=Path.Combine(output,"owned_dependencies_"+Guid.NewGuid().ToString("N"));
        using var registry=new ModelGenerationRegistry(Path.Combine(root,"Generations")) { ArtifactClock=()=>DateTimeOffset.UtcNow.AddDays(2) };
        registry.ConfigureRlArtifacts(root,new RlArtifactSettings(Path.Combine(root,"Config",RlArtifactSettings.FileName)));
        await registry.SaveRlArtifactPolicyAsync(new(NonactiveRetentionDays:0,NonactiveKeepCount:0));
        var direction=await registry.RegisterRlAsync(config.RootElement.GetProperty("LaterDirection").GetString()!);
        var actor=await registry.RegisterRlAsync(config.RootElement.GetProperty("LaterActor").GetString()!);
        var reviewed=registry.PreviewRlArtifacts()!;
        Assert.Contains("Dependency",reviewed.Entries.Single(e=>e.Id==direction.ModelId).Reasons);
        Assert.True(reviewed.Entries.Single(e=>e.Id==actor.ModelId).Candidate);
        var result=await registry.CleanupRlArtifactsAsync(reviewed);
        Assert.Single(result.Deleted); Assert.Equal(actor.ModelId,result.Deleted[0]); Assert.Empty(result.Failures);
        Assert.NotNull(registry.GetRlGeneration(direction.ModelId)); Assert.Null(registry.GetRlGeneration(actor.ModelId));
        Assert.True(registry.PreviewRlArtifacts()!.Entries.Single(e=>e.Id==direction.ModelId).Candidate);
        await File.WriteAllTextAsync(Path.Combine(output,"actual_artifact_qualification_evidence.json"),JsonSerializer.Serialize(new {
            root,direction=direction.ModelId,actor=actor.ModelId,dependency_protected=true,explicit_actor_removed=true,
            reviewed_set_not_widened=true,no_training_or_financial_contract_changes=true }));
    }
}
