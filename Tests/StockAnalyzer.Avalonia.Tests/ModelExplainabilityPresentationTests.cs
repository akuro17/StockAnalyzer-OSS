using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.ML.OnnxRuntime;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>
/// T11/T12 presentation. The explanation and calibration come from the fixture the production Python code wrote
/// (<c>Tests/StockAnalyzer.Core.Tests/Assets/explain_calibration_fixture.onnx*</c>).
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class ModelExplainabilityPresentationTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;
    public ModelExplainabilityPresentationTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private static (ModelExplanation Explanation, ModelCalibration Calibration) Fixtures()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var model = Path.Combine(root.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets", "explain_calibration_fixture.onnx");
        using var session = new InferenceSession(model);
        var metadata = new Dictionary<string, string>(session.ModelMetadata.CustomMetadataMap);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        return (ModelExplanation.Load(model, metadata, hash)!,
            ModelCalibration.Load(model, metadata, hash, new string('a', 64)));
    }

    private static Mock<IModelExplainabilityService> Service(ModelExplanationLoad explanation, ModelCalibrationLoad calibration)
    {
        var service = new Mock<IModelExplainabilityService>();
        service.Setup(s => s.LoadExplanationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(explanation);
        service.Setup(s => s.LoadCalibrationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(calibration);
        return service;
    }

    private static string Text(string key) => LocalizationManager.Instance.Get(key);

    [Fact]
    public async Task Select_PublishesTopFiveSignedContributionsPartialDependenceAndCalibration()
    {
        var (explanation, calibration) = Fixtures();
        var service = Service(new ModelExplanationLoad(ExplanationAvailability.Available, explanation),
            new ModelCalibrationLoad(CalibrationAvailability.Active, calibration));
        using var vm = new ModelExplainabilityViewModel(service.Object, new SynchronousDispatcherService());
        vm.Select("model-1");
        await vm.LoadTask;

        Assert.False(vm.IsLoading);
        Assert.Equal(explanation.Records.Length, vm.Cases.Count);
        Assert.Equal(Text("Explainability_RepresentativeNote"), vm.RepresentativeNote);
        Assert.Contains("probability space", vm.ContributionNote);
        Assert.Contains("market feasibility are not preserved", vm.ContributionNote);
        Assert.Contains("standard error", vm.StabilityNote);
        Assert.Contains(explanation.Stability!.PermutationsUsed + " permutations", vm.StabilityNote);
        foreach (var (display, record) in vm.Cases.Zip(explanation.Records))
        {
            Assert.Equal(record.Outputs.Length, display.Outputs.Count);
            Assert.Contains(record.Sample.Symbol, display.Header);
            foreach (var (shown, output) in display.Outputs.Zip(record.Outputs))
            {
                var top = output.Top();
                Assert.Equal(top.Select(c => c.Channel), shown.Rows.Select(r => r.Channel));
                Assert.True(shown.Rows.Count <= ModelExplanationContract.TopCount);
                Assert.StartsWith(output.OutputId + ":", shown.Title);
                // a bar is drawn on the side of its sign, scaled by the largest magnitude of the output
                foreach (var (row, contribution) in shown.Rows.Zip(top))
                {
                    Assert.Contains(" ± ", row.Display); // every contribution of the fixture carries its standard error
                    Assert.StartsWith(contribution.Value.ToString("+0.0000;-0.0000;0.0000"), row.Display);
                    Assert.Equal(Math.Max(0, contribution.Value), row.PositiveMagnitude);
                    Assert.Equal(Math.Max(0, -contribution.Value), row.NegativeMagnitude);
                    Assert.True(Math.Abs(contribution.Value) <= row.ScaleMaximum + 1e-12);
                }
            }
        }
        Assert.Equal(explanation.Pdp!.Channels.Length, vm.PartialDependenceRows.Count);
        Assert.Contains(explanation.Pdp.Channels[0].Channel, vm.PartialDependenceRows[0].Text);
        Assert.Contains("OHLC consistency is not preserved", vm.PartialDependenceNote);
        Assert.Contains("not the dependence observed in real market data", vm.PartialDependenceNote);
        Assert.All(vm.PartialDependenceRows, r => Assert.Contains("of shifted values outside the training range", r.Text));
        Assert.Contains("model-1", vm.Provenance);
        Assert.Contains(explanation.DataRevision, vm.Provenance);
        Assert.Contains("temperature scaling, T = 1.", vm.CalibrationStatus);
        Assert.Contains("Outer test", vm.CalibrationDetail);
        Assert.Contains("Equal-mass ECE", vm.CalibrationDetail);
        Assert.Null(vm.CalibrationWarning);
    }

    [Fact]
    public async Task Select_OfALegacySidecarShowsPlainContributionsWithoutReliabilityNotes()
    {
        var (explanation, calibration) = Fixtures();
        var legacy = explanation with
        {
            Stability = null,
            Records = explanation.Records.Select(r => r with
            {
                StabilityHalfDelta = null,
                Outputs = r.Outputs.Select(o => o with
                {
                    Contributions = o.Contributions.Select(c => c with { StandardError = null }).ToImmutableArray(),
                }).ToImmutableArray(),
            }).ToImmutableArray(),
            Pdp = explanation.Pdp! with
            {
                Channels = explanation.Pdp.Channels.Select(c => c with { OutOfSupportRate = default }).ToImmutableArray(),
            },
        };
        var vm = new ModelExplainabilityViewModel(
            Service(new ModelExplanationLoad(ExplanationAvailability.Available, legacy),
                new ModelCalibrationLoad(CalibrationAvailability.Active, calibration)).Object,
            new SynchronousDispatcherService());
        vm.Select("m");
        await vm.LoadTask;
        Assert.Null(vm.StabilityNote);
        Assert.All(vm.Cases.SelectMany(c => c.Outputs).SelectMany(o => o.Rows), r => Assert.DoesNotContain("±", r.Display));
        Assert.All(vm.PartialDependenceRows, r => Assert.DoesNotContain("outside the training range", r.Text));
    }

    [Fact]
    public async Task Select_ReportsEveryAbsenceAsAStateNotAnError()
    {
        var (explanation, _) = Fixtures();
        async Task<ModelExplainabilityViewModel> Run(ModelExplanationLoad e, ModelCalibrationLoad c)
        {
            var vm = new ModelExplainabilityViewModel(Service(e, c).Object, new SynchronousDispatcherService());
            vm.Select("m");
            await vm.LoadTask;
            return vm;
        }
        var notGenerated = await Run(new ModelExplanationLoad(ExplanationAvailability.NotGenerated, null),
            new ModelCalibrationLoad(CalibrationAvailability.None));
        Assert.Equal(Text("Explainability_Unavailable_NotGenerated"), notGenerated.ExplanationStatus);
        Assert.Empty(notGenerated.Cases);
        Assert.Equal(Text("Calibration_None"), notGenerated.CalibrationStatus);

        var invalid = await Run(new ModelExplanationLoad(ExplanationAvailability.Invalid, null),
            new ModelCalibrationLoad(CalibrationAvailability.Unavailable, null, "insufficient_samples"));
        Assert.Equal(Text("Explainability_Unavailable_Invalid"), invalid.ExplanationStatus);
        Assert.Contains("insufficient_samples", invalid.CalibrationStatus);

        foreach (var status in new[] { "regression_not_adopted", "no_background", "no_validation", "call_budget_exceeded" })
        {
            var headerOnly = explanation with { Status = status, Records = default, Pdp = null };
            var vm = await Run(new ModelExplanationLoad(ExplanationAvailability.Available, headerOnly),
                new ModelCalibrationLoad(CalibrationAvailability.None));
            Assert.Equal(Text("Explainability_Status_" + status), vm.ExplanationStatus);
            Assert.Empty(vm.Cases);
            Assert.Empty(vm.PartialDependenceRows);
            Assert.Null(vm.RepresentativeNote);
        }
    }

    [Fact]
    public async Task Select_ShowsAFailedExplanationAsFailed_NotAsNeverGenerated()
    {
        var vm = new ModelExplainabilityViewModel(Service(new ModelExplanationLoad(ExplanationAvailability.Failed, null),
            new ModelCalibrationLoad(CalibrationAvailability.None)).Object, new SynchronousDispatcherService());
        vm.Select("m");
        await vm.LoadTask;

        Assert.Equal(Text("Explainability_Unavailable_Failed"), vm.ExplanationStatus);
        Assert.NotEqual(Text("Explainability_Unavailable_NotGenerated"), vm.ExplanationStatus);
    }

    [Fact]
    public async Task Select_ShowsALoadThatThrewAsAFault_NotAsAbsence()
    {
        var service = new Mock<IModelExplainabilityService>();
        service.Setup(s => s.LoadExplanationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("broken sidecar"));
        service.Setup(s => s.LoadCalibrationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("broken sidecar"));
        var vm = new ModelExplainabilityViewModel(service.Object, new SynchronousDispatcherService());
        vm.Select("m");
        await vm.LoadTask;

        Assert.Equal(Text("Explainability_Unavailable_Invalid"), vm.ExplanationStatus);
        Assert.Equal(Text("Calibration_LoadFailed"), vm.CalibrationStatus);
        Assert.NotEqual(Text("Calibration_None"), vm.CalibrationStatus);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Select_ShowsABoundaryCalibrationAsNotApplied_AndOmitsEqualMassEceWhenAbsent()
    {
        var (explanation, calibration) = Fixtures();
        async Task<ModelExplainabilityViewModel> Run(CalibrationAvailability availability, ModelCalibration value)
        {
            var vm = new ModelExplainabilityViewModel(
                Service(new ModelExplanationLoad(ExplanationAvailability.Available, explanation),
                    new ModelCalibrationLoad(availability, value)).Object,
                new SynchronousDispatcherService());
            vm.Select("m");
            await vm.LoadTask;
            return vm;
        }
        var boundary = await Run(CalibrationAvailability.Disabled, calibration with { Status = ModelCalibrationContract.BoundaryStatus, Temperature = 0.01 });
        Assert.Contains("not applied", boundary.CalibrationStatus);
        Assert.Contains("0.01", boundary.CalibrationStatus);
        Assert.Null(boundary.CalibrationDetail);

        var scores = calibration.Evaluation.OuterTest with
        {
            Before = calibration.Evaluation.OuterTest.Before with { EceEqualMass = null },
            After = calibration.Evaluation.OuterTest.After with { EceEqualMass = null },
        };
        var legacy = await Run(CalibrationAvailability.Active, calibration with { Evaluation = calibration.Evaluation with { OuterTest = scores } });
        Assert.Contains("Outer test", legacy.CalibrationDetail);
        Assert.DoesNotContain("Equal-mass", legacy.CalibrationDetail);
    }

    [Fact]
    public async Task Select_FlagsAFittedTemperatureOutsideTheStandardBand()
    {
        var (explanation, calibration) = Fixtures();
        var vm = new ModelExplainabilityViewModel(
            Service(new ModelExplanationLoad(ExplanationAvailability.Available, explanation),
                new ModelCalibrationLoad(CalibrationAvailability.Active, calibration with { Temperature = 8.0 })).Object,
            new SynchronousDispatcherService());
        vm.Select("m");
        await vm.LoadTask;
        Assert.NotNull(vm.CalibrationWarning);
        Assert.Contains("0.25", vm.CalibrationWarning);
    }

    [Fact]
    public async Task Select_DropsAResultThatArrivesForASupersededSelection_AndAfterDisposal()
    {
        var (explanation, calibration) = Fixtures();
        var first = new TaskCompletionSource<ModelExplanationLoad>();
        var second = new TaskCompletionSource<ModelExplanationLoad>();
        var service = new Mock<IModelExplainabilityService>();
        service.Setup(s => s.LoadExplanationAsync("a", It.IsAny<CancellationToken>())).Returns(first.Task);
        service.Setup(s => s.LoadExplanationAsync("b", It.IsAny<CancellationToken>())).Returns(second.Task);
        service.Setup(s => s.LoadCalibrationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelCalibrationLoad(CalibrationAvailability.Active, calibration));
        var vm = new ModelExplainabilityViewModel(service.Object, new SynchronousDispatcherService());
        vm.Select("a");
        var firstTask = vm.LoadTask;
        vm.Select("b");
        var secondTask = vm.LoadTask;
        second.SetResult(new ModelExplanationLoad(ExplanationAvailability.Available, explanation));
        await secondTask;
        first.SetResult(new ModelExplanationLoad(ExplanationAvailability.NotGenerated, null));
        await firstTask;
        Assert.Contains("Model: b", vm.Provenance); // the late result of "a" did not overwrite "b"
        Assert.NotEmpty(vm.Cases);
        Assert.Null(vm.ExplanationStatus);

        vm.Dispose(); // a disposed view model ignores every later publish
        vm.Select("b");
        await vm.LoadTask;
        Assert.Empty(vm.Cases);
        Assert.True(vm.IsLoading);
    }

    [Fact]
    public void Select_WithoutAServiceOrASelectionShowsNothingAndNeverThrows()
    {
        var vm = new ModelExplainabilityViewModel();
        vm.Select("m");
        Assert.False(vm.IsLoading);
        vm.Select(null);
        Assert.Empty(vm.Cases);
        Assert.Null(vm.CalibrationStatus);
    }

    [AvaloniaFact]
    public async Task SettingsPage_RendersTheExplainabilityPanelForTheSelectedGeneration()
    {
        var (explanation, calibration) = Fixtures();
        var service = Service(new ModelExplanationLoad(ExplanationAvailability.Available, explanation),
            new ModelCalibrationLoad(CalibrationAvailability.Active, calibration));
        var settings = new Mock<IPredictionSettingsManager>();
        settings.SetupGet(s => s.WindowSize).Returns(20);
        using var vm = new PredictionModelsViewModel(settings.Object, analysisDispatcher: new SynchronousDispatcherService(),
            explainabilityService: service.Object);
        vm.SelectedGeneration = new ModelGenerationSummary("first", "explain_calibration_fixture.onnx", "classification",
            "daily", 5, .5, new string('a', 64), false);
        await vm.Explainability.LoadTask;
        var view = new PredictionModelsView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 1400 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var text = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(Text("Explainability_Title"), text);
            Assert.Contains(Text("Calibration_Title"), text);
            Assert.Contains(text, t => t is not null && t.Contains("temperature scaling, T = 1."));
            Assert.Contains(text, t => t is not null && t.StartsWith("Up: base "));
            Assert.Contains(text, t => t is not null && t.Contains("Input-shift response"));
            Assert.DoesNotContain(text, t => t is not null && t.Contains("Partial dependence"));
            Assert.Contains(view.GetVisualDescendants().OfType<ProgressBar>(), b => b.Maximum > 0 && b.Maximum <= 1);
            // the panel must not turn a probability-space contribution into a price or percent-axis element
            Assert.DoesNotContain(text, t => t is not null && t.Contains("USD"));
        }
        finally
        {
            window.Close();
        }
    }
}
