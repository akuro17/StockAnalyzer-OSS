using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_list_training_jobs: lists training jobs tracked in the current session (offered only with --enable-execution).
/// </summary>
internal sealed class ListTrainingJobsTool : IMcpTool
{
    private const string LimitArgument = "limit";

    private readonly ISandboxedExecutionGateway _gateway;
    private readonly ErrorMessageSanitizer _sanitizer;

    public ListTrainingJobsTool(ISandboxedExecutionGateway gateway, ErrorMessageSanitizer sanitizer)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
    }

    public string Name => "sa_list_training_jobs";

    public string Description =>
        "Lists training jobs tracked in the current server session, sorted newest first. " +
        "Returns job summaries (jobId, kind, state, stage, elapsedSeconds) without exposing model internals or requests; " +
        "details are retrieved via sa_get_training_status.";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string? ExampleArgumentsJson => "{}";

    public string InputSchemaJson => ToolSchema.Object()
        .Integer(LimitArgument, $"Optional maximum number of jobs to return (1 to {ExecutionLimits.MaxTrackedJobs}). Default {ExecutionLimits.MaxTrackedJobs}.", defaultValue: ExecutionLimits.MaxTrackedJobs, minimum: 1, maximum: ExecutionLimits.MaxTrackedJobs)
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        int limit = ToolArgs.PositiveWithin(arguments, LimitArgument, ExecutionLimits.MaxTrackedJobs, ExecutionLimits.MaxTrackedJobs);

        IReadOnlyList<TrainingJobSummary> summaries = ExecutionToolSupport.Guard(() => _gateway.ListJobs());
        if (summaries.Count > limit)
        {
            summaries = summaries.Take(limit).ToList();
        }

        return Task.FromResult(ToolJson.Build(writer => Write(writer, summaries)));
    }

    private void Write(Utf8JsonWriter writer, IReadOnlyList<TrainingJobSummary> jobs)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("jobs");
        foreach (TrainingJobSummary job in jobs)
        {
            writer.WriteStartObject();
            writer.WriteString(ExecutionToolSupport.JobIdArgument, job.JobId);
            writer.WriteString("kind", ExecutionToolSupport.KindName(job.Kind));
            writer.WriteString("state", job.State.ToString());
            if (job.Stage is null)
            {
                writer.WriteNull("stage");
            }
            else
            {
                writer.WriteString("stage", ExecutionToolSupport.Clean(_sanitizer, job.Stage, ExecutionLimits.MaxStageChars));
            }

            writer.WriteNumber("elapsedSeconds", Math.Round(job.Elapsed.TotalSeconds, 1));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
