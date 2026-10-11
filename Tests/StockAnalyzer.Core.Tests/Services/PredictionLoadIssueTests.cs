#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A model trained with another window than the configured one used to fail silently (an empty fallback, no reason, and the failure was
/// remembered for the model id even after the owner corrected Window Size). The cause is now named and a corrected window retries.
/// </summary>
public sealed class PredictionLoadIssueTests
{
    // trend_predictor_goodmeta.onnx: window 10, OhlcvMinMax, 3 classes.
    private static readonly string Model = Path.Combine("Assets", "trend_predictor_goodmeta.onnx");
    private const int ModelWindow = 10;

    [Fact]
    public void ValidateModelContract_AnotherSequenceLength_ThrowsTheTypedMismatchWithBothWindows()
    {
        var error = Assert.Throws<PredictionWindowMismatchException>(() => PredictionService.ValidateModelContract(
            inputIsTensor: true, inputDimensions: new[] { 1, 7, 5 }, outputIsTensor: true, outputDimensions: new[] { 1, 3 },
            expectedWindowSize: 3, expectedFeaturesPerBar: 5, expectedClassCount: 3, inputNodeName: "input", outputNodeName: "output"));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(7, error.ModelWindow);
        Assert.Equal(3, error.ConfiguredWindow);
    }

    [Fact]
    public async Task AMismatchingWindow_FallsBack_NamesBothWindows_AndRetriesOnceTheWindowIsCorrected()
    {
        var settings = new PredictionServiceTests.TestSettings(Model, predictionWindowSize: ModelWindow + 5);
        using var service = new PredictionService(settings, new MLDataProcessor());
        Assert.Null(service.LoadIssue); // nothing was tried yet

        var failed = await service.PredictAsync(PredictionServiceTests.BuildCandles(40));

        Assert.True(failed.IsFallback);
        var issue = Assert.IsType<PredictionWindowMismatchException>(service.LoadIssue);
        Assert.Equal(ModelWindow, issue.ModelWindow);
        Assert.Equal(ModelWindow + 5, issue.ConfiguredWindow);

        // The same configuration is not retried on every call...
        Assert.True((await service.PredictAsync(PredictionServiceTests.BuildCandles(40))).IsFallback);
        Assert.Same(issue, service.LoadIssue);

        // ...but the owner correcting Window Size is enough: no restart, no model change.
        settings.SetPredictionWindowSize(ModelWindow);
        Assert.Null(service.LoadIssue); // the recorded failure no longer applies to the configuration
        var recovered = await service.PredictAsync(PredictionServiceTests.BuildCandles(40));

        Assert.False(recovered.IsFallback);
        Assert.Null(service.LoadIssue);
    }

    [Fact]
    public async Task AFeatureModeThatDoesNotMatchTheModel_FallsBack_AndRetriesOnceTheModeIsCorrected()
    {
        // The model's own metadata says ohlcv_minmax: another configured mode fails the load, and correcting the mode needs no restart.
        var settings = new PredictionServiceTests.TestSettings(Model, predictionWindowSize: ModelWindow, predictionFeatureMode: PredictionFeatureMode.LogReturn);
        using var service = new PredictionService(settings, new MLDataProcessor());

        Assert.True((await service.PredictAsync(PredictionServiceTests.BuildCandles(40))).IsFallback);
        Assert.True((await service.PredictAsync(PredictionServiceTests.BuildCandles(40))).IsFallback);   // recorded: not retried while nothing changed

        settings.SetPredictionFeatureMode(PredictionFeatureMode.OhlcvMinMax);
        var recovered = await service.PredictAsync(PredictionServiceTests.BuildCandles(40));

        Assert.False(recovered.IsFallback);
    }

    [Fact]
    public async Task AFailureThatIsNotAWindowMismatch_HasNoLoadIssue()
    {
        using var service = new PredictionService(new PredictionServiceTests.TestSettings("does_not_exist.onnx", predictionWindowSize: 3), new MLDataProcessor());

        Assert.True((await service.PredictAsync(PredictionServiceTests.BuildCandles(10))).IsFallback);

        Assert.Null(service.LoadIssue);
    }
}
