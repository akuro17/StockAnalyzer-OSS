using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_start_portfolio_rl_training: starts one reinforcement-learning portfolio smoke run in the temporary sandbox and returns at once (offered only with --enable-execution).</summary>
internal sealed class StartPortfolioRlTrainingTool : IMcpTool
{
    public const string SymbolsArgument = "symbols";
    public const string StartDateArgument = "startDate";
    public const string EndDateExclusiveArgument = "endDateExclusive";
    public const string SymbolHoldoutArgument = "symbolHoldout";
    public const string MaxAssetWeightPercentArgument = "maxAssetWeightPercent";
    public const string ModeArgument = "mode";

    private readonly ISandboxedExecutionGateway _gateway;
    private readonly SymbolResolver _symbols;

    public StartPortfolioRlTrainingTool(ISandboxedExecutionGateway gateway, SymbolResolver symbols)
    {
        _gateway = gateway;
        _symbols = symbols;
    }

    public string Name => "sa_start_portfolio_rl_training";

    public string Description =>
        "Starts one sandboxed reinforcement-learning portfolio training run across 2 to 100 tickers in the temporary sandbox and returns at once with a jobId; " +
        "poll sa_get_training_status until the state is final (sa_cancel_training stops it). " +
        "Executes discrete allocation smoke training (1 seed, 1024 transitions, factory costs). " +
        "No files are written to Data/; artifacts are held in the ephemeral sandbox and cleared upon completion. " +
        "Returns jobId, state, and baseline comparisons (Cash, Equal-Weight Buy & Hold, Equal-Weight Daily Rebalanced returns) once succeeded.";

    public bool IsHeavy => true;

    public bool IsExecution => true;

    public string? ExampleArgumentsJson => "{\"symbols\":[\"7203-T\",\"6758-T\"]}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, "The list of tickers in the portfolio universe (2 to 100 symbols).", required: true, minItems: 2)
        .String(StartDateArgument, $"First date of the period ({ToolJson.DateFormat}). Default: proposed from stored data.")
        .String(EndDateExclusiveArgument, $"First day NOT in the period ({ToolJson.DateFormat}). Default: proposed from stored data.")
        .Boolean(SymbolHoldoutArgument, "Whether symbol holdout is requested (default: true).")
        .Integer(MaxAssetWeightPercentArgument, "Maximum asset weight percent between 1 and 100 (default: 100).", defaultValue: 100, minimum: 1, maximum: 100)
        .String(ModeArgument, "Evaluation mode (Standard, ShortHistory). Default: Standard.", allowedValues: ["Standard", "ShortHistory"])
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? rawSymbols = ToolArgs.StringArray(arguments, SymbolsArgument);
        if (rawSymbols is null || rawSymbols.Count < RlPortfolioContract.MinimumSymbols)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"At least {RlPortfolioContract.MinimumSymbols} symbols are required.", SymbolsArgument);
        }

        if (rawSymbols.Count > ExecutionLimits.MaxPortfolioSymbols)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"At most {ExecutionLimits.MaxPortfolioSymbols} symbols are supported.", SymbolsArgument);
        }

        var resolvedSymbols = new List<string>(rawSymbols.Count);
        foreach (string raw in rawSymbols)
        {
            string resolved = await _symbols.ResolveAsync(raw, SymbolsArgument, cancellationToken).ConfigureAwait(false);
            resolvedSymbols.Add(resolved);
        }

        if (resolvedSymbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != resolvedSymbols.Count)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "A symbol is listed twice.", SymbolsArgument);
        }

        DateOnly? start = ToolArgs.Date(arguments, StartDateArgument);
        DateOnly? end = ToolArgs.Date(arguments, EndDateExclusiveArgument);
        if (start is not null && end is not null && start >= end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{StartDateArgument}' must be before '{EndDateExclusiveArgument}'.", StartDateArgument);
        }

        bool holdout = ToolArgs.Boolean(arguments, SymbolHoldoutArgument) ?? true;
        int minRequiredSymbols = holdout ? RlPortfolioContract.MinimumSymbolsWithHoldout : RlPortfolioContract.MinimumSymbols;
        if (resolvedSymbols.Count < minRequiredSymbols)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Parameter '{SymbolsArgument}' must contain at least {minRequiredSymbols} distinct symbols when {SymbolHoldoutArgument} is {holdout.ToString().ToLowerInvariant()}.",
                SymbolsArgument);
        }

        int maxWeightPercent = ToolArgs.PositiveWithin(arguments, MaxAssetWeightPercentArgument, 100, 100);

        string? modeText = ToolArgs.String(arguments, ModeArgument);
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.Standard;
        if (modeText is not null && !Enum.TryParse(modeText, ignoreCase: true, out mode))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown mode '{modeText}'.", ModeArgument);
        }

        var request = new EphemeralPortfolioRlRequest
        {
            Symbols = resolvedSymbols,
            StartDate = start,
            EndDateExclusive = end,
            SymbolHoldout = holdout,
            MaxAssetWeightPercent = maxWeightPercent,
            Mode = mode,
        };

        TrainingJobTicket ticket;
        try
        {
            ticket = await _gateway.StartPortfolioRlTrainingAsync(request, cancellationToken).ConfigureAwait(false);
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
