using System.Text.Json;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_run_onnx_prediction: runs a model that sa_start_training produced over stored daily bars (offered only with --enable-execution).</summary>
internal sealed class RunOnnxPredictionTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string AsOfDateArgument = "asOfDate";
    private const string CountArgument = "count";
    private const string ModelIdArgument = "modelId";

    /// <summary>The alias of the newest succeeded model (the gateway defines it).</summary>
    private const string LatestAlias = "latest";

    private const TimeFrame PredictionTimeFrame = TimeFrame.D1;

    private readonly ISandboxedExecutionGateway _gateway;
    private readonly CandleWindowLoader _loader;
    private readonly SymbolResolver _symbols;
    private readonly IMLDataProcessor _processor;
    private readonly McpLimits _limits;
    private readonly int _maxPredictionBars;

    public RunOnnxPredictionTool(
        ISandboxedExecutionGateway gateway,
        IReadOnlyStockAnalyzerGateway data,
        SymbolResolver symbols,
        IMLDataProcessor processor,
        McpLimits limits,
        SandboxedExecutionSettings settings)
    {
        _gateway = gateway;
        _loader = new CandleWindowLoader(data);
        _symbols = symbols;
        _processor = processor;
        _limits = limits;
        _maxPredictionBars = settings.MaxPredictionBars;
    }

    public string Name => "sa_run_onnx_prediction";

    public string Description =>
        "Runs a model trained with sa_start_training or a registered production model (modelId) over the stored daily bars of one symbol. " +
        "Exactly one of 'artifactId' or 'modelId' must be specified. " +
        "Each prediction uses only the model's window of bars that ends at its own bar (never a later bar): the latest 'count' bars up to asOfDate are predicted, oldest first. " +
        "A classifier returns classScores (probabilities, the model's own class order), predictedClass (the most probable class) and confidence (its probability); " +
        "a regressor returns value (the predicted log return). modelContract lists the window, feature mode, classes and the sha256 of the model. " +
        $"{McpDisclaimerConstants.ResearchNotAdvice} The model must be a succeeded job's (see sa_get_training_status) or 'latest', or a registered modelId (or 'active').";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string? ExampleArgumentsJson => "{\"modelId\":\"active\",\"symbol\":\"7203-T\",\"count\":10}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(ExecutionToolSupport.ArtifactIdArgument, $"The artifactId of a succeeded job, or '{LatestAlias}' for the newest model.")
        .String(ModelIdArgument, "The identifier of a registered model generation, or 'active' for the current active model.")
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable.", required: true)
        .String(AsOfDateArgument, $"Last bar to predict ({ToolJson.DateFormat}). Default: the latest stored daily bar.")
        .Integer(CountArgument, $"Number of consecutive bars to predict. Default 1, at most {_maxPredictionBars}.", defaultValue: 1, minimum: 1, maximum: _maxPredictionBars)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string? artifactId = ToolArgs.String(arguments, ExecutionToolSupport.ArtifactIdArgument);
        string? modelId = ToolArgs.String(arguments, ModelIdArgument);

        bool hasArtifact = !string.IsNullOrWhiteSpace(artifactId);
        bool hasModel = !string.IsNullOrWhiteSpace(modelId);

        if (hasArtifact == hasModel)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "Exactly one of 'artifactId' or 'modelId' must be specified.",
                hasArtifact ? ModelIdArgument : ExecutionToolSupport.ArtifactIdArgument,
                hint: "Provide 'artifactId' for ephemeral sandbox models, or 'modelId' (e.g. 'active' or generation ID) for registered models.");
        }

        if (hasArtifact)
        {
            ValidateArtifactId(artifactId!);
        }

        string symbol = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, cancellationToken).ConfigureAwait(false);
        DateOnly? asOf = ToolArgs.Date(arguments, AsOfDateArgument);
        int count = ToolArgs.PositiveWithin(arguments, CountArgument, 1, _maxPredictionBars);

        using ArtifactLease lease = ExecutionToolSupport.Guard(() =>
            hasArtifact
                ? _gateway.LeaseArtifact(artifactId!)
                : _gateway.LeaseRegisteredModel(modelId!));
        using EphemeralPredictionModel model = Open(lease);
        EphemeralModelContract contract = model.Contract;
        int lead = model.LeadBars;
        int needed = contract.WindowSize + lead + count - 1;
        if (needed > _limits.MaxCandlesPerRequest)
        {
            // needed = window + lead + count - 1, so the largest count a request may ask for depends on the model's window and lead bars.
            int largestCount = _limits.MaxCandlesPerRequest - (contract.WindowSize + lead) + 1;
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The model needs {contract.WindowSize} bars per prediction; {needed} bars would be read, more than the limit {_limits.MaxCandlesPerRequest}.",
                CountArgument,
                largestCount >= 1
                    ? $"For this model (window {contract.WindowSize}) use a count of at most {largestCount}."
                    : $"The model's window of {contract.WindowSize} bars alone is more than the limit {_limits.MaxCandlesPerRequest}.");
        }

        // Without a date the latest bars are enough; with one, the bars up to the date are read from the whole stored history, however old the date is.
        List<CandleData> bars = asOf is null
            ? (await _loader.LoadAsync(symbol, PredictionTimeFrame, needed, cancellationToken).ConfigureAwait(false)).Bars
            : await _loader.LoadUpToAsync(symbol, PredictionTimeFrame, asOf.Value, needed, cancellationToken).ConfigureAwait(false);
        if (bars.Count < needed)
        {
            string leadNote = lead > 0 ? $", {lead} lead bar(s)" : string.Empty;
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                $"{needed} daily bars are needed ({contract.WindowSize} per prediction{leadNote}, {count} prediction(s)); {bars.Count} are stored{(asOf is null ? string.Empty : $" up to {asOf.Value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture)}")}.",
                hint: "Use a smaller count or an earlier asOfDate that has more history.");
        }

        List<CandleData> used = bars.Skip(bars.Count - needed).ToList();
        IReadOnlyList<EphemeralPrediction> predictions = Predict(model, used, count);
        return ToolJson.Build(writer => Write(writer, lease.Artifact, symbol, contract, used, predictions, modelId));
    }

    private static void ValidateArtifactId(string artifactId)
    {
        if (artifactId != LatestAlias && !StockAnalyzer.Core.Models.Training.TrainingRunId.IsValid(artifactId))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{ExecutionToolSupport.ArtifactIdArgument}' is not an artifact id.",
                ExecutionToolSupport.ArtifactIdArgument,
                $"Use the artifactId from sa_get_training_status, or '{LatestAlias}'.");
        }
    }

    // A model that does not follow the contract is not a client mistake and not a missing model: it is reported as an unexpected error with its reason.
    private EphemeralPredictionModel Open(ArtifactLease lease)
    {
        try
        {
            return EphemeralPredictionModel.Open(lease, _processor);
        }
        catch (EphemeralModelContractException error)
        {
            throw new ToolException(ToolErrorCodes.InternalError, error.Message);
        }
    }

    private static IReadOnlyList<EphemeralPrediction> Predict(EphemeralPredictionModel model, List<CandleData> history, int count)
    {
        try
        {
            return model.PredictLast(history, count);
        }
        catch (EphemeralModelContractException error)
        {
            throw new ToolException(ToolErrorCodes.InternalError, error.Message);
        }
        catch (InvalidDataException error)
        {
            // Stored bars that are not strictly chronological.
            throw new ToolException(ToolErrorCodes.DataUnavailable, error.Message);
        }
    }

    private static void Write(
        Utf8JsonWriter writer,
        EphemeralArtifact artifact,
        string symbol,
        EphemeralModelContract contract,
        List<CandleData> used,
        IReadOnlyList<EphemeralPrediction> predictions,
        string? modelId)
    {
        writer.WriteStartObject();
        if (modelId is not null)
        {
            writer.WriteString(ModelIdArgument, modelId);
            writer.WriteString("resolvedModelId", artifact.ArtifactId);
        }
        else
        {
            writer.WriteString(ExecutionToolSupport.ArtifactIdArgument, artifact.ArtifactId);
        }
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", PredictionTimeFrame.ToString());
        writer.WriteString("asOfDate", ToolJson.FormatDate(used[^1].Timestamp));
        writer.WriteStartObject("modelContract");
        writer.WriteNumber("windowSize", contract.WindowSize);
        writer.WriteString("featureMode", contract.FeatureMode);
        writer.WriteString("targetType", contract.IsRegression ? "Regression" : "Classification");
        writer.WriteNumber("classCount", contract.ClassLabels.Count);
        writer.WriteNumber("horizonBars", contract.HorizonBars);
        writer.WriteString("sha256", contract.Sha256);
        writer.WriteEndObject();
        writer.WriteStartArray("predictions");
        int firstBar = used.Count - predictions.Count;
        for (int index = 0; index < predictions.Count; index++)
        {
            EphemeralPrediction prediction = predictions[index];
            writer.WriteStartObject();
            writer.WriteString("date", ToolJson.FormatDate(used[firstBar + index].Timestamp));
            if (contract.IsRegression)
            {
                writer.WriteNumber("value", prediction.Value!.Value);
            }
            else
            {
                writer.WriteStartArray("classScores");
                foreach (ClassScore score in prediction.Scores)
                {
                    writer.WriteStartObject();
                    writer.WriteString("class", score.Label);
                    writer.WriteNumber("probability", score.Score);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteString("predictedClass", prediction.PredictedClass);
                writer.WriteNumber("confidence", prediction.Confidence!.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
