using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class ListTrainingJobsToolTests
{
    private readonly FakeExecutionGateway _gateway = new();
    private readonly ErrorMessageSanitizer _sanitizer = new(new[] { @"C:\test\data", @"/test/data" });

    private ListTrainingJobsTool CreateTool() => new(_gateway, _sanitizer);

    [Fact]
    public void Properties_ConformToContract()
    {
        var tool = CreateTool();
        Assert.Equal("sa_list_training_jobs", tool.Name);
        Assert.False(tool.IsHeavy);
        Assert.True(tool.IsExecution);
        Assert.NotNull(tool.Description);
        Assert.Contains("sa_get_training_status", tool.Description);
    }

    [Fact]
    public async Task EmptyJobs_ReturnsEmptyJobsArray()
    {
        var tool = CreateTool();
        _gateway.Jobs = () => Array.Empty<TrainingJobSummary>();

        string json = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("jobs", out var jobsElement));
        Assert.Equal(JsonValueKind.Array, jobsElement.ValueKind);
        Assert.Equal(0, jobsElement.GetArrayLength());
    }

    [Fact]
    public async Task ReturnsJobsInOrderWithCorrectMappings()
    {
        var tool = CreateTool();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var summaries = new List<TrainingJobSummary>
        {
            new("job-1", TrainingJobKind.Supervised, TrainingJobState.Running, "epoch 10", TimeSpan.FromSeconds(12.34), now),
            new("job-2", TrainingJobKind.ReinforcementLearning, TrainingJobState.Succeeded, null, TimeSpan.FromSeconds(45.67), now.AddMinutes(-5)),
            new("job-3", TrainingJobKind.PortfolioReinforcementLearning, TrainingJobState.Failed, "evaluating", TimeSpan.FromSeconds(89.12), now.AddMinutes(-10)),
        };
        _gateway.Jobs = () => summaries;

        string json = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var jobs = doc.RootElement.GetProperty("jobs");

        Assert.Equal(3, jobs.GetArrayLength());

        // Job 1
        var j0 = jobs[0];
        Assert.Equal("job-1", j0.GetProperty("jobId").GetString());
        Assert.Equal("supervised", j0.GetProperty("kind").GetString());
        Assert.Equal("Running", j0.GetProperty("state").GetString());
        Assert.Equal("epoch 10", j0.GetProperty("stage").GetString());
        Assert.Equal(12.3, j0.GetProperty("elapsedSeconds").GetDouble());

        // Job 2
        var j1 = jobs[1];
        Assert.Equal("job-2", j1.GetProperty("jobId").GetString());
        Assert.Equal("reinforcement_learning", j1.GetProperty("kind").GetString());
        Assert.Equal("Succeeded", j1.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, j1.GetProperty("stage").ValueKind);
        Assert.Equal(45.7, j1.GetProperty("elapsedSeconds").GetDouble());

        // Job 3
        var j2 = jobs[2];
        Assert.Equal("job-3", j2.GetProperty("jobId").GetString());
        Assert.Equal("portfolio_reinforcement_learning", j2.GetProperty("kind").GetString());
        Assert.Equal("Failed", j2.GetProperty("state").GetString());
        Assert.Equal("evaluating", j2.GetProperty("stage").GetString());
        Assert.Equal(89.1, j2.GetProperty("elapsedSeconds").GetDouble());

        // Verify zero leak of model internals / request details
        foreach (var job in jobs.EnumerateArray())
        {
            Assert.False(job.TryGetProperty("artifact", out _));
            Assert.False(job.TryGetProperty("metrics", out _));
            Assert.False(job.TryGetProperty("request", out _));
            Assert.False(job.TryGetProperty("rlResult", out _));
            Assert.False(job.TryGetProperty("portfolioRlResult", out _));
            Assert.False(job.TryGetProperty("messages", out _));
        }
    }

    [Fact]
    public async Task LimitArgument_CapsReturnedJobs()
    {
        var tool = CreateTool();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var summaries = new List<TrainingJobSummary>();
        for (int i = 0; i < 10; i++)
        {
            summaries.Add(new($"job-{i}", TrainingJobKind.Supervised, TrainingJobState.Succeeded, null, TimeSpan.FromSeconds(i), now.AddMinutes(-i)));
        }
        _gateway.Jobs = () => summaries;

        string json = await tool.InvokeAsync(JsonDocument.Parse("""{"limit": 4}""").RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var jobs = doc.RootElement.GetProperty("jobs");
        Assert.Equal(4, jobs.GetArrayLength());
        Assert.Equal("job-0", jobs[0].GetProperty("jobId").GetString());
        Assert.Equal("job-3", jobs[3].GetProperty("jobId").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidLimit_ThrowsInvalidArgument(int invalidLimit)
    {
        var tool = CreateTool();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.InvokeAsync(JsonDocument.Parse($"{{\"limit\": {invalidLimit}}}").RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal("limit", ex.Parameter);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(100)]
    public async Task OverLimit_ThrowsLimitExceeded(int invalidLimit)
    {
        var tool = CreateTool();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.InvokeAsync(JsonDocument.Parse($"{{\"limit\": {invalidLimit}}}").RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.LimitExceeded, ex.Code);
        Assert.Equal("limit", ex.Parameter);
    }

    [Fact]
    public async Task ObligationC_StageSanitizesAbsolutePaths()
    {
        var tool = CreateTool();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var summaries = new List<TrainingJobSummary>
        {
            new("job-sec", TrainingJobKind.Supervised, TrainingJobState.Running, @"processing C:\test\data\sub\weights.onnx now", TimeSpan.FromSeconds(5), now)
        };
        _gateway.Jobs = () => summaries;

        string json = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.DoesNotContain(@"C:\test\data", json, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        string stage = doc.RootElement.GetProperty("jobs")[0].GetProperty("stage").GetString()!;
        Assert.Contains(ErrorMessageSanitizer.Placeholder, stage);
    }
}
