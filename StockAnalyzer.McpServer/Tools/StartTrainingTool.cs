using System.Text.Json;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_start_training: starts one supervised training job in the temporary sandbox and returns at once (offered only with --enable-execution).</summary>
internal sealed class StartTrainingTool : IMcpTool
{
    /// <summary>Where the files of a job are: told to the client without a path (the folder is private to the server).</summary>
    public const string ArtifactsLocation = "os-temp";

    private const string HyperparameterPrefix = TrainingRequestJson.HyperparametersArgument + ".";

    private readonly ISandboxedExecutionGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly McpLimits _limits;

    public StartTrainingTool(ISandboxedExecutionGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _limits = limits;
    }

    public string Name => "sa_start_training";

    public string Description =>
        "Starts one supervised training job (the Training Wizard's path) on stored daily bars and returns at once with a jobId; poll sa_get_training_status until the state is final. " +
        "The model is written to a temporary folder that is removed when the server stops: nothing is saved under Data and nothing is registered. " +
        "Fixed by the server: daily bars, OHLCV min-max features, a fresh model, no ensemble. " +
        "Frameworks, architectures and hyperparameters (names, defaults, ranges) are listed in sa_get_capabilities under execution. " +
        "Only a limited number of jobs run at once (EXECUTION_BUSY). A server whose temporary folder path is too long for the trainer is refused before anything is created (SANDBOX_PATH_TOO_LONG).";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string? ExampleArgumentsJson => "{\"symbols\":[\"7203-T\"],\"framework\":\"LightGbm\",\"architecture\":\"LightGbmStandard\",\"windowSize\":30,\"horizon\":5}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(TrainingRequestJson.SymbolsArgument, $"Symbols to train on, 1 to {_limits.MaxSymbolsPerMetadataCall}, none twice ('.' and '-' are interchangeable).", required: true, minItems: 1)
        .String(TrainingRequestJson.StartDateArgument, $"First date of the training data ({ToolJson.DateFormat}). Default: the start of the stored data.")
        .String(TrainingRequestJson.EndDateArgument, $"Last date of the training data ({ToolJson.DateFormat}). Default: the end of the stored data.")
        .String(TrainingRequestJson.FrameworkArgument, "Training framework.", required: true, allowedValues: Enum.GetNames<TrainingFramework>())
        .String(TrainingRequestJson.ArchitectureArgument, "Architecture of the framework (see sa_get_capabilities, execution.architectures).", required: true)
        .Integer(TrainingRequestJson.WindowSizeArgument, "Input length in bars.", required: true)
        .Integer(TrainingRequestJson.HorizonArgument, "How many bars ahead the label looks.", required: true)
        .String(TrainingRequestJson.TargetTypeArgument, "Learning objective. Default Classification.", allowedValues: Enum.GetNames<TargetType>())
        .Integer(TrainingRequestJson.SplitsArgument, "Number of walk-forward splits.", defaultValue: WalkForwardDataRequirement.DefaultSplitCount)
        .Boolean(TrainingRequestJson.FixedZScoreArgument, "Standardize features with population Z-score. Default false.")
        .Number(TrainingRequestJson.ClipSigmaArgument, "Optional positive outlier clipping multiple after Z-score (for example 3.0).")
        .Boolean(TrainingRequestJson.CalibrationArgument, "Fitted temperature probability calibration for classification. Default false.")
        .Boolean(TrainingRequestJson.CpcvArgument, "Combinatorial purged cross-validation evaluation. Default false.")
        .FreeObject(TrainingRequestJson.HyperparametersArgument, "Trainer hyperparameters by key (see sa_get_capabilities, execution.hyperparameters); a value is a string or a number. A key left out keeps the trainer's default.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> requested = ToolArgs.StringArray(arguments, TrainingRequestJson.SymbolsArgument)!;
        if (requested.Count > _limits.MaxSymbolsPerMetadataCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"Argument '{TrainingRequestJson.SymbolsArgument}' lists more than {_limits.MaxSymbolsPerMetadataCall} symbols.",
                TrainingRequestJson.SymbolsArgument,
                $"Use {_limits.MaxSymbolsPerMetadataCall} or fewer.");
        }

        var symbols = new List<string>(requested.Count);
        for (int index = 0; index < requested.Count; index++)
        {
            symbols.Add(await _symbols.ResolveAsync(requested[index], $"{TrainingRequestJson.SymbolsArgument}[{index}]", cancellationToken).ConfigureAwait(false));
        }

        DateOnly? start = ToolArgs.Date(arguments, TrainingRequestJson.StartDateArgument);
        DateOnly? end = ToolArgs.Date(arguments, TrainingRequestJson.EndDateArgument);
        if (start > end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{TrainingRequestJson.StartDateArgument}' is after '{TrainingRequestJson.EndDateArgument}'.", TrainingRequestJson.StartDateArgument);
        }

        bool? fixedZScore = ToolArgs.Boolean(arguments, TrainingRequestJson.FixedZScoreArgument);
        double? clipSigma = ToolArgs.Number(arguments, TrainingRequestJson.ClipSigmaArgument);
        bool effectiveFixedZScore = fixedZScore ?? EphemeralFixedValues.Current.FixedZScore;
        if (clipSigma is { } sigma)
        {
            if (!double.IsFinite(sigma) || sigma <= 0)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{TrainingRequestJson.ClipSigmaArgument}' must be positive.", TrainingRequestJson.ClipSigmaArgument);
            }

            if (!effectiveFixedZScore)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{TrainingRequestJson.ClipSigmaArgument}' requires '{TrainingRequestJson.FixedZScoreArgument}' to be true.", TrainingRequestJson.ClipSigmaArgument);
            }
        }

        bool? calibration = ToolArgs.Boolean(arguments, TrainingRequestJson.CalibrationArgument);
        bool? cpcv = ToolArgs.Boolean(arguments, TrainingRequestJson.CpcvArgument);

        TargetType targetType = ToolArgs.String(arguments, TrainingRequestJson.TargetTypeArgument) is { } target ? Enum.Parse<TargetType>(target) : TargetType.Classification;
        bool effectiveCalibration = calibration ?? EphemeralFixedValues.Current.Calibration;
        if (effectiveCalibration && targetType != TargetType.Classification)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{TrainingRequestJson.CalibrationArgument}' is only supported when '{TrainingRequestJson.TargetTypeArgument}' is 'Classification'.", TrainingRequestJson.CalibrationArgument);
        }

        var request = new EphemeralTrainingRequest
        {
            Symbols = symbols,
            StartDate = start,
            EndDate = end,
            Framework = Enum.Parse<TrainingFramework>(ToolArgs.String(arguments, TrainingRequestJson.FrameworkArgument)!),
            Architecture = ToolArgs.String(arguments, TrainingRequestJson.ArchitectureArgument)!,
            WindowSize = ToInt(arguments, TrainingRequestJson.WindowSizeArgument)!.Value,
            Horizon = ToInt(arguments, TrainingRequestJson.HorizonArgument)!.Value,
            TargetType = targetType,
            NSplits = ToInt(arguments, TrainingRequestJson.SplitsArgument) ?? WalkForwardDataRequirement.DefaultSplitCount,
            FixedZScore = effectiveFixedZScore,
            ClipSigma = clipSigma,
            Calibration = effectiveCalibration,
            Cpcv = cpcv ?? EphemeralFixedValues.Current.Cpcv,
            Hyperparameters = Hyperparameters(arguments),
        };

        TrainingJobTicket ticket;
        try
        {
            ticket = await _gateway.StartTrainingAsync(request, cancellationToken).ConfigureAwait(false);
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
            writer.WriteString("artifactsLocation", ArtifactsLocation);
            writer.WriteEndObject();
        });
    }

    // The schema check guarantees an integer that fits a long; one that does not fit an int is refused by name (never wrapped around).
    private static int? ToInt(JsonElement arguments, string name)
    {
        long? value = ToolArgs.Integer(arguments, name);
        if (value is null)
        {
            return null;
        }

        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' is out of range.", name);
        }

        return (int)value;
    }

    private static IReadOnlyDictionary<string, string> Hyperparameters(JsonElement arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(TrainingRequestJson.HyperparametersArgument, out JsonElement values))
        {
            return result;
        }

        foreach (JsonProperty pair in values.EnumerateObject())
        {
            string text = pair.Value.ValueKind switch
            {
                JsonValueKind.String => pair.Value.GetString()!,
                JsonValueKind.Number => pair.Value.GetRawText(),
                _ => throw new ToolException(
                    ToolErrorCodes.InvalidArgument, $"Hyperparameter '{pair.Name}' must be a string or a number.", HyperparameterPrefix + pair.Name),
            };
            if (!result.TryAdd(pair.Name, text))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Hyperparameter '{pair.Name}' is given twice.", HyperparameterPrefix + pair.Name);
            }
        }

        return result;
    }
}
