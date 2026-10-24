using System.Text.Json;
using StockAnalyzer.Core.Execution;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_cancel_training: asks a running training job to stop (offered only with --enable-execution).</summary>
internal sealed class CancelTrainingTool : IMcpTool
{
    private readonly ISandboxedExecutionGateway _gateway;

    public CancelTrainingTool(ISandboxedExecutionGateway gateway)
    {
        _gateway = gateway;
    }

    public string Name => "sa_cancel_training";

    public string Description =>
        "Asks a training job (supervised or reinforcement learning) to stop and returns its state: Cancelling while the trainer is being stopped, Cancelled once it is. " +
        "A job that already ended keeps its final state. A cancelled job leaves no model behind.";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string InputSchemaJson => ToolSchema.Object()
        .String(ExecutionToolSupport.JobIdArgument, "The jobId returned by sa_start_training.", required: true)
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string jobId = ExecutionToolSupport.JobId(arguments);
        TrainingJobState state = ExecutionToolSupport.Guard(() => _gateway.Cancel(jobId));
        return Task.FromResult(ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(ExecutionToolSupport.JobIdArgument, jobId);
            writer.WriteString("state", state.ToString());
            writer.WriteEndObject();
        }));
    }
}
