using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_model_explanation: loads the generation-bound offline explanation, temperature calibration,
/// and optional regime assignment for a registered model generation. Zero-mutation.
/// </summary>
internal sealed class GetModelExplanationTool : IMcpTool
{
    private const string ModelIdArgument = "modelId";
    private const string SymbolArgument = "symbol";
    private const string AsOfDateArgument = "asOfDate";

    private const int MinimumCandlesForRegime = ModelAnalysisContract.VolatilityBars + 1;

    // Why no regime was assigned (regimeReason). Fixed words, tested in this order; the key is null when a regime was assigned.
    private const string ReasonAnalysisUnavailable = "analysis_unavailable";
    private const string ReasonTimeframeMismatch = "timeframe_mismatch";
    private const string ReasonInsufficientBars = "insufficient_bars";
    private const string ReasonNotAssignable = "not_assignable";

    private readonly ReadOnlyModelGenerationRegistry _registry;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly IModelExplainabilityService _explainabilityService;
    private readonly IModelAnalysisService _analysisService;

    public GetModelExplanationTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        ReadOnlyModelGenerationRegistry? registry = null,
        IModelExplainabilityService? explainabilityService = null,
        IModelAnalysisService? analysisService = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _registry = registry ?? new ReadOnlyModelGenerationRegistry();
        _explainabilityService = explainabilityService ?? new ModelExplainabilityService(_registry);
        _analysisService = analysisService ?? new ModelAnalysisService(_registry, new MLDataProcessor());
    }

    public string Name => "sa_get_model_explanation";

    public string Description =>
        "Loads offline explanation (Shapley values, partial dependence) and temperature calibration " +
        "for a registered model generation. Optionally assigns the market regime when symbol is provided, on bars of the model's own time frame; " +
        "regimeReason names why none was assigned (null when one was). " +
        "The explanation, calibration and regime centroids are fixed artefacts of the training run: asOfDate only selects the bars used for the regime. " +
        "pointInTime gives dataEnd (the last prediction anchor of the training run, null when unknown) and horizonBars (bars of the model's time frame): " +
        "the labels of the last anchor reach horizonBars bars beyond dataEnd, and asOfBeforeDataEnd is true when asOfDate < dataEnd (null when unknown, never false then). " +
        "Zero-mutation; contains no file paths.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"modelId\":\"active\",\"symbol\":\"7203-T\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(ModelIdArgument, "The identifier of a registered model generation, or 'active' for the current active model.", required: true)
        .String(SymbolArgument, "Optional canonical ticker symbol (e.g. 7203-T) to compute regime assignment for.")
        .String(AsOfDateArgument, $"Optional evaluation date ({ToolJson.DateFormat}); defaults to the latest stored bar.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (modelIdRaw, resolvedModelId, manifest) = RegisteredModelResolver.Resolve(_registry, arguments, ModelIdArgument);

        ModelExplanationLoad explanationLoad = await _explainabilityService.LoadExplanationAsync(resolvedModelId, cancellationToken).ConfigureAwait(false);
        ModelCalibrationLoad calibrationLoad = await _explainabilityService.LoadCalibrationAsync(resolvedModelId, cancellationToken).ConfigureAwait(false);

        DateOnly? asOf = ToolArgs.Date(arguments, AsOfDateArgument);
        string? rawSymbol = ToolArgs.String(arguments, SymbolArgument);
        if (asOf.HasValue && rawSymbol is null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{AsOfDateArgument}' requires '{SymbolArgument}' to be specified.", AsOfDateArgument);
        }

        string? resolvedSymbol = rawSymbol is null
            ? null
            : await _symbols.ResolveAsync(rawSymbol, SymbolArgument, cancellationToken).ConfigureAwait(false);

        // Read once, with or without a symbol: the training period belongs to the model, not to the symbol asked about.
        ModelAnalysisSnapshot? analysis = await TryLoadAnalysisAsync(resolvedModelId, cancellationToken).ConfigureAwait(false);

        RegimeAssignment? regimeAssignment = null;
        string? regimeReason = null;
        if (resolvedSymbol is not null)
        {
            (regimeAssignment, regimeReason) = await AssignRegimeAsync(analysis, resolvedSymbol, manifest, asOf, cancellationToken).ConfigureAwait(false);
        }

        var pointInTime = new PointInTime(LastTrainingAnchor(analysis), manifest.Horizon, asOf);

        return ToolJson.Build(writer => Write(
            writer,
            modelIdRaw,
            resolvedModelId,
            explanationLoad,
            calibrationLoad,
            pointInTime,
            regimeAssignment,
            regimeReason,
            resolvedSymbol is not null));
    }

    /// <summary>
    /// What the explanation, the calibration and the regime centroids rest on. <c>DataEnd</c> is the last prediction anchor of any symbol of the
    /// training run (null when there is no analysis); the labels of that anchor reach <c>HorizonBars</c> bars further, in the model's time frame.
    /// </summary>
    private readonly record struct PointInTime(DateTime? DataEnd, int HorizonBars, DateOnly? AsOf);

    private static DateTime? LastTrainingAnchor(ModelAnalysisSnapshot? analysis)
        => analysis is null || analysis.Analysis.Periods.IsDefaultOrEmpty ? null : analysis.Analysis.Periods.Max(p => p.AnchorEnd);

    /// <summary>
    /// The analysis is optional: a sidecar that is missing, cannot be verified or cannot be read means "no analysis", never a failed call, and its
    /// message (which may carry a path) is dropped. Only cancellation passes through.
    /// </summary>
    private async Task<ModelAnalysisSnapshot?> TryLoadAnalysisAsync(string modelId, CancellationToken cancellationToken)
    {
        try
        {
            return await _analysisService.LoadAsync(modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Assigns the regime on bars of the model's own time frame. When none is assigned the reason is the first that applies, in the order of
    /// the constants below; the first two are known before any bar is read. They repeat three of the checks of
    /// <see cref="IModelAnalysisService.Assign"/>, which is shared with the chart and stays unchanged; a test keeps the two in step.
    /// </summary>
    private async Task<(RegimeAssignment? Assignment, string? Reason)> AssignRegimeAsync(
        ModelAnalysisSnapshot? snapshot,
        string symbol,
        ModelGenerationManifest manifest,
        DateOnly? asOf,
        CancellationToken cancellationToken)
    {
        if (snapshot is null || snapshot.Analysis.Regime.Status != ModelAnalysisContract.AvailableStatus)
        {
            return (null, ReasonAnalysisUnavailable);
        }

        if (!RegisteredModelResolver.TryGetTimeFrame(manifest, out RegisteredModelResolver.ModelTimeFrame modelFrame)
            || !snapshot.Analysis.Timeframe.Equals(modelFrame.Type.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (null, ReasonTimeframeMismatch);
        }

        IReadOnlyList<CandleData> candles = asOf.HasValue
            ? await _gateway.GetCandlesUpToAsync(symbol, modelFrame.Frame, asOf.Value, MinimumCandlesForRegime, cancellationToken).ConfigureAwait(false)
            : await _gateway.GetCandlesAsync(symbol, modelFrame.Frame, MinimumCandlesForRegime, cancellationToken).ConfigureAwait(false);
        if (candles.Count < MinimumCandlesForRegime)
        {
            return (null, ReasonInsufficientBars);
        }

        RegimeAssignment? assignment = _analysisService.Assign(snapshot, candles, modelFrame.Type);
        return assignment is null ? (null, ReasonNotAssignable) : (assignment, null);
    }

    private static void Write(
        Utf8JsonWriter writer,
        string requestedModelId,
        string resolvedModelId,
        ModelExplanationLoad explanationLoad,
        ModelCalibrationLoad calibrationLoad,
        PointInTime pointInTime,
        RegimeAssignment? regime,
        string? regimeReason,
        bool symbolRequested)
    {
        writer.WriteStartObject();
        writer.WriteString("modelId", requestedModelId);
        writer.WriteString("resolvedModelId", resolvedModelId);

        // Explanation
        writer.WriteStartObject("explanation");
        writer.WriteString("availability", explanationLoad.Availability.ToString());
        if (explanationLoad.Availability == ExplanationAvailability.Available && explanationLoad.Explanation is { } exp)
        {
            if (exp.Method is not null) writer.WriteString("method", exp.Method);
            if (exp.OutputSpace is not null) writer.WriteString("outputSpace", exp.OutputSpace);
            writer.WriteNumber("windowSize", exp.WindowSize);
            writer.WriteString("timeframe", exp.Timeframe);
            writer.WriteString("status", exp.Status);

            writer.WriteStartArray("baseChannels");
            foreach (var ch in exp.BaseChannels) writer.WriteStringValue(ch);
            writer.WriteEndArray();

            writer.WriteStartArray("orderedChannels");
            foreach (var ch in exp.OrderedChannels) writer.WriteStringValue(ch);
            writer.WriteEndArray();

            if (!exp.Classes.IsDefaultOrEmpty)
            {
                writer.WriteStartArray("classes");
                foreach (var cls in exp.Classes) writer.WriteStringValue(cls);
                writer.WriteEndArray();
            }

            if (exp.ModelCalls.HasValue) writer.WriteNumber("modelCalls", exp.ModelCalls.Value);
            if (exp.Permutations.HasValue) writer.WriteNumber("permutations", exp.Permutations.Value);
            if (exp.BackgroundSize.HasValue) writer.WriteNumber("backgroundSize", exp.BackgroundSize.Value);

            if (exp.Population is { } pop)
            {
                writer.WriteStartObject("population");
                writer.WriteString("background", pop.Background);
                writer.WriteString("explained", pop.Explained);
                writer.WriteNumber("validationSampleCount", pop.ValidationSampleCount);
                writer.WriteEndObject();
            }

            if (exp.Stability is { } stab)
            {
                writer.WriteStartObject("stability");
                writer.WriteNumber("permutationsUsed", stab.PermutationsUsed);
                writer.WriteNumber("maxStandardError", stab.MaxStandardError);
                writer.WriteNumber("maxHalfDelta", stab.MaxHalfDelta);
                writer.WriteEndObject();
            }

            if (!exp.Records.IsDefaultOrEmpty)
            {
                writer.WriteStartArray("records");
                foreach (var rec in exp.Records)
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("sample");
                    writer.WriteString("symbol", rec.Sample.Symbol);
                    writer.WriteString("anchorDate", ToolJson.FormatDate(rec.Sample.AnchorDate));
                    writer.WriteNumber("anchorRow", rec.Sample.AnchorRow);
                    writer.WriteEndObject();

                    writer.WriteStartArray("reasons");
                    foreach (var r in rec.Reasons) writer.WriteStringValue(r);
                    writer.WriteEndArray();

                    writer.WriteStartArray("outputs");
                    foreach (var o in rec.Outputs)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("outputId", o.OutputId);
                        writer.WriteNumber("baseValue", o.BaseValue);
                        writer.WriteNumber("prediction", o.Prediction);

                        writer.WriteStartArray("contributions");
                        foreach (var c in o.Contributions)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("channel", c.Channel);
                            writer.WriteNumber("value", c.Value);
                            if (c.StandardError.HasValue) writer.WriteNumber("standardError", c.StandardError.Value);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();

                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    if (rec.StabilityHalfDelta.HasValue)
                        writer.WriteNumber("stabilityHalfDelta", rec.StabilityHalfDelta.Value);

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (exp.Pdp is { } pdp)
            {
                writer.WriteStartObject("pdp");
                writer.WriteNumber("gridPoints", pdp.GridPoints);
                writer.WriteString("semantics", pdp.Semantics);

                writer.WriteStartArray("quantileLevels");
                foreach (var q in pdp.QuantileLevels) writer.WriteNumberValue(q);
                writer.WriteEndArray();

                writer.WriteStartArray("channels");
                foreach (var ch in pdp.Channels)
                {
                    writer.WriteStartObject();
                    writer.WriteString("channel", ch.Channel);

                    writer.WriteStartArray("offsets");
                    foreach (var off in ch.Offsets) writer.WriteNumberValue(off);
                    writer.WriteEndArray();

                    writer.WriteStartObject("values");
                    foreach (var kvp in ch.Values)
                    {
                        writer.WriteStartArray(kvp.Key);
                        foreach (var v in kvp.Value) writer.WriteNumberValue(v);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();

                    if (!ch.OutOfSupportRate.IsDefaultOrEmpty)
                    {
                        writer.WriteStartArray("outOfSupportRate");
                        foreach (var r in ch.OutOfSupportRate) writer.WriteNumberValue(r);
                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
        }
        writer.WriteEndObject();

        // Calibration
        writer.WriteStartObject("calibration");
        writer.WriteString("availability", calibrationLoad.Availability.ToString());
        if (calibrationLoad.Reason is not null) writer.WriteString("reason", calibrationLoad.Reason);
        if (calibrationLoad.Calibration is { } cal)
        {
            writer.WriteString("method", cal.Method);
            writer.WriteString("score", cal.Score);
            writer.WriteNumber("temperature", cal.Temperature);
            writer.WriteNumber("epsilon", cal.Epsilon);
            writer.WriteString("status", cal.Status);

            writer.WriteStartObject("fit");
            writer.WriteString("population", cal.Fit.Population);
            writer.WriteNumber("sampleCount", cal.Fit.SampleCount);
            writer.WriteString("fitDataRevision", cal.Fit.FitDataRevision);
            writer.WriteNumber("eceBins", cal.Fit.EceBins);
            writer.WriteStartArray("periods");
            foreach (var p in cal.Fit.Periods)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", p.Symbol);
                writer.WriteString("kind", p.Kind);
                writer.WriteString("anchorStart", ToolJson.FormatDate(p.AnchorStart));
                writer.WriteString("anchorEnd", ToolJson.FormatDate(p.AnchorEnd));
                writer.WriteNumber("sampleCount", p.SampleCount);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("evaluation");
            WriteCalibrationMetrics(writer, "calibrationSet", cal.Evaluation.CalibrationSet);
            WriteCalibrationMetrics(writer, "outerTest", cal.Evaluation.OuterTest);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        // Point in time: the explanation is a fixed artefact of the training run and does not follow asOfDate.
        writer.WriteStartObject("pointInTime");
        writer.WriteBoolean("explanationIsStatic", true);
        if (pointInTime.DataEnd is { } dataEnd)
        {
            writer.WriteString("dataEnd", ToolJson.FormatDate(dataEnd));
        }
        else
        {
            writer.WriteNull("dataEnd");
        }

        writer.WriteNumber("horizonBars", pointInTime.HorizonBars);
        if (pointInTime.AsOf is { } asOf)
        {
            // Strictly before the last anchor. Unknown stays null: false would read as "no overlap".
            if (pointInTime.DataEnd is { } end)
            {
                writer.WriteBoolean("asOfBeforeDataEnd", asOf < DateOnly.FromDateTime(end));
            }
            else
            {
                writer.WriteNull("asOfBeforeDataEnd");
            }
        }

        writer.WriteEndObject();

        // Regime
        if (symbolRequested)
        {
            if (regime is not null)
            {
                writer.WriteStartObject("regime");
                writer.WriteNumber("id", regime.Id);
                writer.WriteString("anchorDate", ToolJson.FormatDate(regime.Anchor));
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("regime");
            }

            if (regimeReason is not null)
            {
                writer.WriteString("regimeReason", regimeReason);
            }
            else
            {
                writer.WriteNull("regimeReason");
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteCalibrationMetrics(Utf8JsonWriter writer, string propertyName, CalibrationMetrics metrics)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("sampleCount", metrics.SampleCount);

        writer.WriteStartObject("before");
        writer.WriteNumber("nll", metrics.Before.Nll);
        writer.WriteNumber("brier", metrics.Before.Brier);
        writer.WriteNumber("ece", metrics.Before.Ece);
        if (metrics.Before.EceEqualMass.HasValue) writer.WriteNumber("eceEqualMass", metrics.Before.EceEqualMass.Value);
        writer.WriteEndObject();

        writer.WriteStartObject("after");
        writer.WriteNumber("nll", metrics.After.Nll);
        writer.WriteNumber("brier", metrics.After.Brier);
        writer.WriteNumber("ece", metrics.After.Ece);
        if (metrics.After.EceEqualMass.HasValue) writer.WriteNumber("eceEqualMass", metrics.After.EceEqualMass.Value);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }
}
