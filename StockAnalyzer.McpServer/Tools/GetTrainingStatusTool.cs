using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Execution;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_get_training_status: where one training job is, its latest messages, its metrics, and (when it succeeded) its model (offered only with --enable-execution).</summary>
internal sealed class GetTrainingStatusTool : IMcpTool
{
    private const string Ellipsis = "...";
    private const string SupervisedKind = "supervised";
    private const string ReinforcementLearningKind = "reinforcement_learning";
    private const string PortfolioReinforcementLearningKind = "portfolio_reinforcement_learning";

    private readonly ISandboxedExecutionGateway _gateway;
    private readonly ErrorMessageSanitizer _sanitizer;

    public GetTrainingStatusTool(ISandboxedExecutionGateway gateway, ErrorMessageSanitizer sanitizer)
    {
        _gateway = gateway;
        _sanitizer = sanitizer;
    }

    public string Name => "sa_get_training_status";

    public string Description =>
        "Returns the state of a job started with sa_start_training, sa_start_rl_training, or sa_start_portfolio_rl_training (kind says which): Running, Cancelling, Validating, Succeeded, Failed or Cancelled (the last three are final). " +
        "stage is the trainer's current step and progressPercent is 0 to 100 (0 until the first progress report). metrics, when present, hold the latest numbers the trainer reported (null where a number is not finite). " +
        "messages are the latest status lines; request echoes the settings actually used. " +
        "When Succeeded, artifact describes the model (artifactId, file name, size, sha256 and whether it loads with its input and output shapes, -1 for a dynamic dimension) and sa_run_onnx_prediction can use it; " +
        "when Failed, failureReason is TIMEOUT, INVALID_ONNX or TRAINING_FAILED. The model lives in a temporary folder and is removed when the server stops; " +
        "to keep a model, repeat the run in the Training Wizard with the echoed request and register it there. " +
        "A reinforcement-learning job (kind reinforcement_learning) echoes its period in request, has no metrics or artifact, and when Succeeded reports result: " +
        "validationMeanReturn (exact decimal text), candidateSeed, checkpointStep, trainingTransitions, model size and sha256, and the source revision; its files are removed when it ends. " +
        "A portfolio reinforcement-learning job (kind portfolio_reinforcement_learning) echoes its parameters in request, and when Succeeded reports policyReturn and baselineComparison (cashReturn, equalBuyHoldReturn, equalDailyRebalancedReturn), selectedSeed, selectedCheckpoint, hashes and model identity.";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string? ExampleArgumentsJson => "{\"jobId\":\"00000000-0000-0000-0000-000000000000\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(ExecutionToolSupport.JobIdArgument, "The jobId returned by sa_start_training, sa_start_rl_training, or sa_start_portfolio_rl_training.", required: true)
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string jobId = ExecutionToolSupport.JobId(arguments);
        TrainingJobStatus status = ExecutionToolSupport.Guard(() => _gateway.GetStatus(jobId));
        return Task.FromResult(ToolJson.Build(writer => Write(writer, status)));
    }

    private void Write(Utf8JsonWriter writer, TrainingJobStatus status)
    {
        writer.WriteStartObject();
        writer.WriteString(ExecutionToolSupport.JobIdArgument, status.JobId);
        string kindString = status.Kind switch
        {
            TrainingJobKind.ReinforcementLearning => ReinforcementLearningKind,
            TrainingJobKind.PortfolioReinforcementLearning => PortfolioReinforcementLearningKind,
            _ => SupervisedKind,
        };
        writer.WriteString("kind", kindString);
        writer.WriteString("state", status.State.ToString());
        if (status.Stage is null)
        {
            writer.WriteNull("stage");
        }
        else
        {
            writer.WriteString("stage", Clean(status.Stage, ExecutionLimits.MaxStageChars));
        }

        writer.WriteNumber("progressPercent", status.ProgressPercent);
        writer.WriteNumber("elapsedSeconds", Math.Round(status.Elapsed.TotalSeconds, 1));
        if (status.Metrics is not null)
        {
            writer.WriteStartObject("metrics");
            foreach (KeyValuePair<string, double> metric in status.Metrics.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                if (double.IsFinite(metric.Value))
                {
                    writer.WriteNumber(metric.Key, metric.Value);
                }
                else
                {
                    writer.WriteNull(metric.Key);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteStartArray("messages");
        foreach (string message in status.Messages)
        {
            writer.WriteStringValue(Clean(message, ExecutionLimits.MaxStatusMessageChars));
        }

        writer.WriteEndArray();
        writer.WritePropertyName("request");
        if (status.Kind == TrainingJobKind.ReinforcementLearning)
        {
            WriteRlRequest(writer, status.RlRequest!);
        }
        else if (status.Kind == TrainingJobKind.PortfolioReinforcementLearning)
        {
            WritePortfolioRlRequest(writer, status.PortfolioRlRequest!);
        }
        else
        {
            TrainingRequestJson.Write(writer, status.Request!);
        }

        if (status.RlResult is { } rl)
        {
            writer.WriteStartObject("result");
            writer.WriteString("validationMeanReturn", rl.ValidationMeanReturn); // the exact decimal text of the report, never a double
            writer.WriteNumber("candidateSeed", rl.CandidateSeed);
            writer.WriteNumber("checkpointStep", rl.CheckpointStep);
            writer.WriteNumber("trainingTransitions", rl.TrainingTransitions);
            writer.WriteStartObject("model");
            writer.WriteNumber("sizeBytes", rl.ModelSizeBytes);
            writer.WriteString("sha256", rl.ModelSha256);
            writer.WriteEndObject();
            writer.WriteString("sourceRevision", rl.SourceRevision);
            writer.WriteEndObject();
        }

        if (status.PortfolioRlResult is { } prl)
        {
            writer.WriteStartObject("result");
            writer.WriteString("policyReturn", prl.PolicyReturn);
            if (prl.EvaluatedBlockCount is { } blockCount)
            {
                writer.WriteNumber("evaluatedBlockCount", blockCount);
            }
            if (prl.PolicyCompoundReturn is { } policyComp)
            {
                writer.WriteString("policyCompoundReturn", policyComp.ToString("F6", CultureInfo.InvariantCulture));
            }
            writer.WriteStartObject("baselineComparison");
            writer.WriteString("cashReturn", prl.CashReturn);
            writer.WriteString("equalBuyHoldReturn", prl.EqualBuyHoldReturn);
            writer.WriteString("equalDailyRebalancedReturn", prl.EqualDailyRebalancedReturn);
            if (prl.CashCompoundReturn is { } cashComp)
            {
                writer.WriteString("cashCompoundReturn", cashComp.ToString("F6", CultureInfo.InvariantCulture));
            }
            if (prl.EqualBuyHoldCompoundReturn is { } ebhComp)
            {
                writer.WriteString("equalBuyHoldCompoundReturn", ebhComp.ToString("F6", CultureInfo.InvariantCulture));
            }
            if (prl.EqualDailyRebalancedCompoundReturn is { } edrComp)
            {
                writer.WriteString("equalDailyRebalancedCompoundReturn", edrComp.ToString("F6", CultureInfo.InvariantCulture));
            }
            writer.WriteEndObject();
            writer.WriteNumber("selectedSeed", prl.SelectedSeed);
            writer.WriteNumber("selectedCheckpoint", prl.SelectedCheckpoint);
            writer.WriteString("universeSha256", prl.UniverseSha256);
            writer.WriteString("calendarSha256", prl.CalendarSha256);
            writer.WriteStartObject("model");
            writer.WriteNumber("sizeBytes", prl.ModelSizeBytes);
            writer.WriteString("sha256", prl.ModelSha256);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (status.Artifact is { } artifact)
        {
            writer.WriteStartObject("artifact");
            writer.WriteString(ExecutionToolSupport.ArtifactIdArgument, artifact.ArtifactId);
            writer.WriteString("onnxFileName", artifact.OnnxFileName);
            writer.WriteNumber("sizeBytes", artifact.SizeBytes);
            writer.WriteString("sha256", artifact.Sha256);
            writer.WriteStartObject("validation");
            writer.WriteBoolean("loads", artifact.Loads);
            WriteShape(writer, "inputShape", artifact.InputShape);
            WriteShape(writer, "outputShape", artifact.OutputShape);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (status.FailureReason is { } reason)
        {
            writer.WriteString("failureReason", FailureReasonName(reason));
        }

        writer.WriteEndObject();
    }

    private static void WriteRlRequest(Utf8JsonWriter writer, EphemeralRlRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString(StartRlTrainingTool.SymbolArgument, request.Symbol);
        writer.WriteString(StartRlTrainingTool.StartDateArgument, request.StartDate!.Value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString(StartRlTrainingTool.EndDateExclusiveArgument, request.EndDateExclusive!.Value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteBoolean("smokeOnly", true);
        writer.WriteEndObject();
    }

    private static void WritePortfolioRlRequest(Utf8JsonWriter writer, EphemeralPortfolioRlRequest request)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("symbols");
        foreach (string symbol in request.Symbols)
        {
            writer.WriteStringValue(symbol);
        }
        writer.WriteEndArray();
        writer.WriteString("startDate", request.StartDate!.Value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("endDateExclusive", request.EndDateExclusive!.Value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteBoolean("symbolHoldout", request.SymbolHoldout);
        writer.WriteNumber("maxAssetWeightPercent", request.MaxAssetWeightPercent);
        writer.WriteString("mode", request.Mode.ToString());
        writer.WriteBoolean("smokeOnly", true);
        writer.WriteEndObject();
    }

    private static void WriteShape(Utf8JsonWriter writer, string name, IReadOnlyList<long> shape)
    {
        writer.WriteStartArray(name);
        foreach (long dimension in shape)
        {
            writer.WriteNumberValue(dimension);
        }

        writer.WriteEndArray();
    }

    /// <summary>The wire name of a failure reason (the plan's three words).</summary>
    public static string FailureReasonName(TrainingFailureReason reason) => reason switch
    {
        TrainingFailureReason.Timeout => "TIMEOUT",
        TrainingFailureReason.InvalidOnnx => "INVALID_ONNX",
        TrainingFailureReason.TrainingFailed => "TRAINING_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No wire name is defined for this failure reason."),
    };

    // Paths removed first (a known root is replaced alone, so the rest of the path is dropped as well), then cut: the replacement is shorter than a path,
    // so the limit still holds.
    private string Clean(string text, int maxChars)
    {
        string clean = RestOfRemovedPath.Replace(_sanitizer.Sanitize(text), ErrorMessageSanitizer.Placeholder);
        return clean.Length > maxChars ? clean[..(maxChars - Ellipsis.Length)] + Ellipsis : clean;
    }

    private static readonly Regex RestOfRemovedPath = new(
        Regex.Escape(ErrorMessageSanitizer.Placeholder) + @"[\\/][^\s""']*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
