#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Characterization of the forecast identity formulas of single-model prediction monitoring. The expected values are written here
/// as independent expressions (not by calling production helpers), so the tests hold before and after the formulas are moved into helpers.
/// </summary>
public sealed class PredictionMonitoringFormulaTests : IDisposable
{
    private const string Symbol = "FORMULA";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_formula_" + Guid.NewGuid().ToString("N"));
    private readonly RetrainingSchedulerTests.MutableClock _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private ModelGenerationRegistry? _registry;

    public void Dispose()
    {
        _registry?.Dispose();
        TestDirectory.Delete(_root);
    }

    internal static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Asserts every identity field of a single-model forecast against the documented formulas.</summary>
    internal static void AssertSingleModelForecast(MonitoredPrediction actual, ModelGenerationManifest manifest,
        IReadOnlyDictionary<string, string> metadata, PredictionResult result, string symbol, TimeframeType timeframe,
        IReadOnlyList<CandleData> inputs)
    {
        var contract = result.OutputContract!;
        decimal threshold = decimal.Parse(metadata[PredictionModelMetadata.NeutralThresholdKey],
            NumberStyles.Float, CultureInfo.InvariantCulture);
        string target = Sha256Hex(string.Create(CultureInfo.InvariantCulture,
            $"{contract.Semantic}|{contract.TargetFormula}|{contract.Timeframe}|{contract.HorizonBars}|{threshold}"));
        string feature = FixedScaler.ComputeContractHash(metadata);
        var anchor = new DateTimeOffset(DateTime.SpecifyKind(inputs[^1].Timestamp, DateTimeKind.Utc));
        string id = Sha256Hex(string.Create(CultureInfo.InvariantCulture,
            $"{manifest.ModelId}|{symbol}|{timeframe}|{anchor:O}|{feature}|{target}"));

        Assert.Equal(id, actual.PredictionId);
        Assert.Equal(manifest.ModelId, actual.ModelId);
        Assert.Equal(manifest.ModelHash, actual.ModelHash);
        Assert.Equal(target, actual.TargetContractHash);
        Assert.Equal(feature, actual.FeatureContractHash);
        Assert.Equal(symbol, actual.Symbol);
        Assert.Equal(timeframe, actual.Timeframe);
        Assert.Equal(anchor, actual.AnchorUtc);
        Assert.Equal(contract.HorizonBars, actual.HorizonBars);
        Assert.Equal(threshold, actual.NeutralThreshold);
        Assert.Equal(result.Label, actual.PredictedLabel);
        Assert.Equal(PredictionLogService.SourceRevision(inputs), actual.InputRevision);
    }

    private static CandleData[] Inputs() =>
        SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray();

    private static (ModelGenerationManifest Manifest, IReadOnlyDictionary<string, string> Metadata) Fixture(
        ModelGenerationRegistry registry, PredictionResult result)
    {
        using var session = new InferenceSession(Path.Combine("Assets", "analysis_fixture.onnx"));
        return (registry.GetManifest(result.ModelId!)!, new Dictionary<string, string>(session.ModelMetadata.CustomMetadataMap));
    }

    [Theory]
    [InlineData("0.005")]
    [InlineData("0")]
    [InlineData("1e-3")]
    [InlineData("-0.1")]
    [InlineData("-0")]
    [InlineData("")]
    [InlineData(" 0.5 ")]
    [InlineData("abc")]
    [InlineData("0,5")]
    [InlineData(null)]
    public void TryReadNeutralThreshold_EqualsTheOriginalInlineExpression(string? text)
    {
        var metadata = new Dictionary<string, string>();
        if (text is not null) metadata[PredictionModelMetadata.NeutralThresholdKey] = text;

        // The expression that PrepareAsync contained before the helper was extracted.
        bool originalInvalid = !decimal.TryParse(metadata.GetValueOrDefault(PredictionModelMetadata.NeutralThresholdKey),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var originalThreshold) || originalThreshold < 0;

        bool valid = PredictionMonitoringReducer.TryReadNeutralThreshold(metadata, out var threshold);

        Assert.Equal(!originalInvalid, valid);
        if (valid) Assert.Equal(originalThreshold, threshold);
    }

    [Fact]
    public void TryReadNeutralThreshold_KnownValues()
    {
        Assert.True(PredictionMonitoringReducer.TryReadNeutralThreshold(
            new Dictionary<string, string> { [PredictionModelMetadata.NeutralThresholdKey] = "0.005" }, out var value));
        Assert.Equal(0.005m, value);
        Assert.False(PredictionMonitoringReducer.TryReadNeutralThreshold(
            new Dictionary<string, string> { [PredictionModelMetadata.NeutralThresholdKey] = "-0.1" }, out _));
        Assert.False(PredictionMonitoringReducer.TryReadNeutralThreshold(new Dictionary<string, string>(), out _));
    }

    [Theory]
    [InlineData("Up", true)]
    [InlineData("Down", true)]
    [InlineData("Neutral", true)]
    [InlineData("Unknown", false)]
    [InlineData("up", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMonitorableLabel_AcceptsExactlyTheThreeClassLabels(string? label, bool expected) =>
        Assert.Equal(expected, PredictionMonitoringReducer.IsMonitorableLabel(label));

    [Theory]
    [InlineData("110", "100", "0.05")]
    [InlineData("90", "100", "0.05")]
    [InlineData("102", "100", "0.05")]
    [InlineData("100", "100", "0")]
    public void EveryObservedLabel_IsMonitorable_AndTheUnknownLabelNever(string future, string anchor, string threshold)
    {
        Assert.True(PredictionMonitoringReducer.IsMonitorableLabel(
            PredictionLogService.ActualLabel(decimal.Parse(anchor, CultureInfo.InvariantCulture), decimal.Parse(future, CultureInfo.InvariantCulture),
                decimal.Parse(threshold, CultureInfo.InvariantCulture))));
        Assert.False(PredictionMonitoringReducer.IsMonitorableLabel(PredictionResult.UnknownLabel));
        Assert.Equal("Unknown", PredictionResult.UnknownLabel);                  // the value the stored audits and the documentation name
        Assert.Equal(PredictionResult.UnknownLabel, PredictionResult.Empty.Label);
    }

    [Fact]
    public void TargetContractHashAndForecastId_EqualTheDocumentedExpressions()
    {
        var contract = new PredictionOutputContract(PredictionOutputSemantic.ClassProbabilities, PredictionOutputUnit.Probability,
            5, TimeframeType.Weekly, ConfidenceType.ClassProbability, PredictionTargetFormula.ThresholdedSimpleReturn);
        var anchor = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

        string target = PredictionMonitoringReducer.TargetContractHash(contract, 0.005m);
        Assert.Equal(Sha256Hex("ClassProbabilities|ThresholdedSimpleReturn|Weekly|5|0.005"), target);
        Assert.Equal(Sha256Hex("m|S|Weekly|2026-09-28T00:00:00.0000000+00:00|f|" + target),
            PredictionMonitoringReducer.ForecastId("m", "S", TimeframeType.Weekly, anchor, "f", target));
        // The confidence type does not take part in the target contract.
        Assert.Equal(target, PredictionMonitoringReducer.TargetContractHash(
            contract with { ConfidenceType = ConfidenceType.CalibratedProbability }, 0.005m));
    }

    [Fact]
    public async Task JsonBackend_SingleModelForecast_FollowsTheDocumentedFormulas()
    {
        _registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var model = await _registry.RegisterAsync(Path.Combine("Assets", "analysis_fixture.onnx"));
        using var session = new InferenceSession(Path.Combine("Assets", "analysis_fixture.onnx"));
        var result = new PredictionResult("Up", 1, [new("Up", 1)])
        {
            ModelId = model.ModelId,
            OutputContract = PredictionModelMetadata.ReadOutputContract(session.ModelMetadata.CustomMetadataMap),
        };
        var monitor = new PredictionLogService(_registry, _clock, Path.Combine(_root, "monitor"));
        var inputs = Inputs();

        var id = await monitor.RecordAsync(result, Symbol, TimeframeType.Daily, inputs);

        Assert.NotNull(id);
        var audit = await monitor.ReadAuditAsync(id!);
        var (manifest, metadata) = Fixture(_registry, result);
        AssertSingleModelForecast(audit!.Prediction, manifest, metadata, result, Symbol, TimeframeType.Daily, inputs);
    }

    [Fact]
    public async Task SqliteBackend_SingleModelForecast_FollowsTheDocumentedFormulas()
    {
        using var kit = new SqliteMonitoringTestKit();
        await kit.EnsureRegistryAsync();
        var service = await kit.CreateServiceAsync();
        var inputs = Inputs();

        var id = await service.RecordAsync(kit.Result!, Symbol, TimeframeType.Daily, inputs);

        Assert.NotNull(id);
        var audit = await service.ReadAuditAsync(id!);
        var (manifest, metadata) = Fixture(kit.Registry!, kit.Result!);
        AssertSingleModelForecast(audit!.Prediction, manifest, metadata, kit.Result!, Symbol, TimeframeType.Daily, inputs);
    }
}
