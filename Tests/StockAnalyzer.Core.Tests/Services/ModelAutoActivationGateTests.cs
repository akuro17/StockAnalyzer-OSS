using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Owner decision D4: the automatic activation after a registration is stopped for a model the chart cannot use; a manual activation never is.</summary>
public sealed class ModelAutoActivationGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_gate_" + Guid.NewGuid().ToString("N"));
    private const string Revision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Classes = "Up,Down,Neutral";

    public ModelAutoActivationGateTests() => Directory.CreateDirectory(_root);
    public void Dispose() => TestDirectory.Delete(_root);

    private string Metrics(string json)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".onnx.metrics.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string Source(double score, double? logLoss, int? reaching)
        => RegistrableGeneration.Create(_root, "model.onnx", score, Revision, logLoss: logLoss, reaching: reaching);

    private static IStockAnalyzerSettings Threshold(float value)
    {
        var settings = new Mock<IStockAnalyzerSettings>();
        settings.SetupGet(s => s.PredictionConfidenceThreshold).Returns(value);
        return settings.Object;
    }

    // ---------------------------------------------------------------------------------------------- the rule

    [Theory]
    [InlineData("{\"fold_multi_logloss\":1.2}", 0.5f, false, true)]                                   // not better than a uniform guess
    [InlineData("{\"fold_multi_logloss\":1.0986122886681098}", 0.5f, false, true)]                    // exactly ln 3 is not better
    [InlineData("{\"fold_multi_logloss\":1.05}", null, false, false)]
    [InlineData("{\"confidence_reach\":{\"bins\":100,\"n\":2,\"max_probability_histogram\":[]}}", 0.5f, false, false)]   // damaged histogram: unknown
    [InlineData("{\"fold_macro_f1\":0.3}", 0.5f, false, false)]                                       // nothing to judge by
    public void TheDecisionStopsOnlyWhatTheMetricsSay(string json, float? threshold, bool reachIsZero, bool notBetter)
    {
        var decision = ModelAutoActivationGate.Evaluate(Metrics(json), Classes, threshold);

        Assert.Equal(reachIsZero, decision.ReachIsZero);
        Assert.Equal(notBetter, decision.NotBetterThanUniform);
        Assert.Equal(reachIsZero || notBetter, decision.Stopped);
    }

    [Fact]
    public void AZeroReachStopsOnlyWhenTheThresholdIsKnown_AndASmallReachDoesNot()
    {
        string Histogram(int reaching) => "{\"confidence_reach\":{\"bins\":100,\"n\":100,\"max_probability_histogram\":[" +
            string.Join(',', Enumerable.Range(0, 100).Select(bin => bin == 36 ? 100 - reaching : bin == 55 ? reaching : 0)) + "]}}";

        Assert.True(ModelAutoActivationGate.Evaluate(Metrics(Histogram(0)), Classes, 0.5f).ReachIsZero);
        Assert.False(ModelAutoActivationGate.Evaluate(Metrics(Histogram(0)), Classes, null).Stopped);        // no threshold: no judgement
        Assert.False(ModelAutoActivationGate.Evaluate(Metrics(Histogram(1)), Classes, 0.5f).Stopped);        // one window reaches it
        Assert.False(ModelAutoActivationGate.Evaluate(Metrics(Histogram(0)), Classes, 0.3f).Stopped);        // the data's largest probability is above 0.3
    }

    [Fact]
    public void AThresholdBetweenTwoBinsNeverStopsAModelWhoseWindowsCouldReachIt()
    {
        // 100 windows: 90 in bin 36 and 10 in bin 50 (probability in [0.50, 0.51)). With threshold 0.505 the lower bound of the reach is 0 (bins from 51),
        // but those 10 windows may be at or above 0.505, so the activation must not be stopped. With 0.51 nothing can reach it.
        var json = "{\"confidence_reach\":{\"bins\":100,\"n\":100,\"max_probability_histogram\":[" +
            string.Join(',', Enumerable.Range(0, 100).Select(bin => bin == 36 ? 90 : bin == 50 ? 10 : 0)) + "]}}";
        var path = Metrics(json);

        Assert.Equal(0.0, ModelConfidenceReach.ShareAtOrAbove(path, 0.505f));
        Assert.False(ModelAutoActivationGate.Evaluate(path, Classes, 0.505f).Stopped);
        Assert.True(ModelAutoActivationGate.Evaluate(path, Classes, 0.51f).ReachIsZero);
        Assert.False(ModelAutoActivationGate.Evaluate(path, Classes, 0.5f).Stopped);
    }

    [Fact]
    public void ARegressionModelOrAMissingFileIsNeverStopped()
    {
        Assert.False(ModelAutoActivationGate.Evaluate(Metrics("{\"fold_multi_logloss\":9}"), "", 0.5f).Stopped);   // no classes: not a classification
        Assert.False(ModelAutoActivationGate.Evaluate(Path.Combine(_root, "missing.onnx.metrics.json"), Classes, 0.5f).Stopped);
    }

    // ---------------------------------------------------------------------------------------------- the deployment service

    [Fact]
    public async Task AFirstGenerationThatIsNotBetterThanGuessing_IsRegisteredButNotActivated()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "r1"));
        var service = new ModelDeploymentService(registry: registry, settings: Threshold(0.5f));

        var manifest = await service.RegisterGenerationAsync(Source(0.3, logLoss: 1.2, reaching: null));

        Assert.Equal(manifest.ModelId, Assert.Single(registry.List()).ModelId);
        Assert.Null(registry.ActiveId);
    }

    [Fact]
    public async Task AFirstGenerationThatNeverReachesTheThreshold_IsRegisteredButNotActivated_UnlessTheThresholdIsUnknown()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "r2"));
        await new ModelDeploymentService(registry: registry, settings: Threshold(0.5f)).RegisterGenerationAsync(Source(0.3, logLoss: 1.0, reaching: 0));
        Assert.Null(registry.ActiveId);

        var manifest = await new ModelDeploymentService(registry: registry).RegisterGenerationAsync(Source(0.31, logLoss: 1.0, reaching: 0));   // no settings
        Assert.Equal(manifest.ModelId, registry.ActiveId);
    }

    [Fact]
    public async Task AGenerationThatReachesTheThresholdAndBeatsGuessing_IsActivatedAsBefore_AndNoMetricsEvidenceMeansNoStop()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "r3"));
        var service = new ModelDeploymentService(registry: registry, settings: Threshold(0.5f));

        var first = await service.RegisterGenerationAsync(Source(0.3, logLoss: 1.0, reaching: 10));
        Assert.Equal(first.ModelId, registry.ActiveId);

        var second = await service.RegisterGenerationAsync(Source(0.4, logLoss: null, reaching: null));   // no log-loss, no histogram
        Assert.Equal(second.ModelId, registry.ActiveId);
    }

    [Fact]
    public async Task WhenStopped_TheActiveModelIsKept_AndChoosingTheGenerationByHandStillWorks()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "r4"));
        var service = new ModelDeploymentService(registry: registry, settings: Threshold(0.5f));
        var good = await service.RegisterGenerationAsync(Source(0.3, logLoss: 1.0, reaching: 10));
        Assert.Equal(good.ModelId, registry.ActiveId);

        // a higher score would normally replace the active model; the gate keeps it
        var better = await service.RegisterGenerationAsync(Source(0.6, logLoss: 1.2, reaching: 0));
        Assert.Equal(good.ModelId, registry.ActiveId);
        Assert.Equal(2, registry.List().Count);

        Assert.True(await registry.ActivateAsync(better.ModelId, manual: true));
        Assert.Equal(better.ModelId, registry.ActiveId);
    }
}
