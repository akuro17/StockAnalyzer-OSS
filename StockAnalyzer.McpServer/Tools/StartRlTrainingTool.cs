using System.Text.Json;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_start_rl_training: starts one reinforcement-learning smoke run in the temporary sandbox and returns at once (offered only with --enable-execution).</summary>
internal sealed class StartRlTrainingTool : IMcpTool
{
    public const string SymbolArgument = "symbol";
    public const string StartDateArgument = "startDate";
    public const string EndDateExclusiveArgument = "endDateExclusive";

    private readonly ISandboxedExecutionGateway _gateway;
    private readonly SymbolResolver _symbols;

    public StartRlTrainingTool(ISandboxedExecutionGateway gateway, SymbolResolver symbols)
    {
        _gateway = gateway;
        _symbols = symbols;
    }

    public string Name => "sa_start_rl_training";

    public string Description =>
        "Starts one reinforcement-learning smoke run (the trial run of the RL window: one seed, " + RlContract.SmokeTrainingTransitions + " transitions, the Backtest window's factory costs) on the stored daily bars of one symbol " +
        "and returns at once with a jobId; poll sa_get_training_status until the state is final (sa_cancel_training stops it). " +
        "It needs a stored equity with its metadata and the application's managed training Python environment (PYTHON_ENV_MISSING otherwise). " +
        "Nothing is saved under Data and no model is registered: a succeeded job reports its validation mean return, seed, checkpoint, model size and sha256, and its files are removed at once. " +
        "Omitted dates are proposed from the stored data as the RL window does (end = the day after the latest final bar). The jobs share one limit with the supervised ones (EXECUTION_BUSY). A server whose temporary folder path is too long for the trainer is refused before anything is created (SANDBOX_PATH_TOO_LONG).";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "The symbol to train on ('.' and '-' are interchangeable).", required: true)
        .String(StartDateArgument, $"First date of the period ({ToolJson.DateFormat}). Default: the earliest start from which the required bars are stored.")
        .String(EndDateExclusiveArgument, $"First day NOT in the period ({ToolJson.DateFormat}); the last stored bar is the day before. Default: the day after the latest final stored bar.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string symbol = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, cancellationToken).ConfigureAwait(false);
        DateOnly? start = ToolArgs.Date(arguments, StartDateArgument);
        DateOnly? end = ToolArgs.Date(arguments, EndDateExclusiveArgument);
        if (start >= end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{StartDateArgument}' is not before '{EndDateExclusiveArgument}'.", StartDateArgument);
        }

        TrainingJobTicket ticket;
        try
        {
            ticket = await _gateway.StartRlTrainingAsync(new EphemeralRlRequest { Symbol = symbol, StartDate = start, EndDateExclusive = end }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (ExecutionToolSupport.TryMap(error, out ToolException mapped))
        {
            throw mapped;
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(ExecutionToolSupport.JobIdArgument, ticket.JobId);
            writer.WriteString("state", ticket.State.ToString());
            writer.WriteString("artifactsLocation", StartTrainingTool.ArtifactsLocation);
            writer.WriteEndObject();
        });
    }
}
