using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Optimization;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Optimization;

public sealed class HardwareConcurrencyPolicyTests
{
    [Theory]
    [InlineData(10, false, 1, 16, 4, 10)]
    [InlineData(20, false, 1, 64, 4, 20)]
    [InlineData(10, true, 1, 16, 1, 1)]
    [InlineData(10, true, 1, 16, 2, 1)]
    [InlineData(10, true, 1, 16, 4, 2)]
    [InlineData(10, true, 1, 16, 6, 4)]
    [InlineData(10, true, 1, 16, 12, 10)]
    [InlineData(10, true, 1, 16, 18, 16)]
    [InlineData(10, true, 1, 16, 32, 16)]
    public void ResolveEffectiveParallelism_AppliesReserveAndSsoTCaps(
        int configured,
        bool autoScale,
        int min,
        int max,
        int cores,
        int expected)
    {
        int actual = HardwareConcurrencyPolicy.ResolveEffectiveParallelism(
            configured,
            autoScale,
            min,
            max,
            HardwareConcurrencyPolicy.DefaultReservedCores,
            processorCountOverride: cores);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveEffectiveParallelism_InvalidBounds_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HardwareConcurrencyPolicy.ResolveEffectiveParallelism(4, true, 0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => HardwareConcurrencyPolicy.ResolveEffectiveParallelism(4, true, 8, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => HardwareConcurrencyPolicy.ResolveEffectiveParallelism(4, true, 1, 16, reservedCores: -1));
    }

    [Fact]
    public void StockAnalyzerSettings_AutoScaleWithCustomManualValue_LogsWarningOncePerConflict()
    {
        var logger = new CapturingLogger<StockAnalyzerSettings>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        _ = new StockAnalyzerSettings(
            config,
            Options.Create(new PythonSettings()),
            Options.Create(new ChartDefaultSettings()),
            Options.Create(new PredictionSettings()),
            Options.Create(new ScreenerSettings()),
            Options.Create(new SmartScreenerSettings()),
            Options.Create(new InfrastructureSettings { ScreenerMaxParallelism = 4, AutoScaleScreenerParallelism = true }),
            Options.Create(new MarketStructureSettings()),
            Options.Create(new PatternRecognitionSettings()),
            Options.Create(new ResilienceSettings()),
            Options.Create(new LocalizationSettings()),
            logger: logger,
            scatterMetricOptions: Options.Create(new ScatterMetricSettings { MaxParallelism = 6, AutoScaleParallelism = true }));

        Assert.Contains(logger.Entries, e => e.Message.Contains("AutoScaleScreenerParallelism"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("ScatterMetrics:AutoScaleParallelism"));
    }

    [Fact]
    public void PythonChildEnvironment_SeparatesInteractiveAndTrainingProfiles_WithoutOverwritingExistingVars()
    {
        var interactive = new ProcessStartInfo();
        interactive.Environment[PythonChildEnvironment.PolarsMaxThreadsVariable] = "99";
        PythonChildEnvironment.ApplyInteractiveWorkerThreadCaps(interactive, processorCountOverride: 16);

        Assert.Equal("1", interactive.Environment[PythonChildEnvironment.OmpNumThreadsVariable]);
        Assert.Equal("1", interactive.Environment[PythonChildEnvironment.OpenBlasNumThreadsVariable]);
        Assert.Equal("1", interactive.Environment[PythonChildEnvironment.MklNumThreadsVariable]);
        Assert.Equal("99", interactive.Environment[PythonChildEnvironment.PolarsMaxThreadsVariable]);

        var interactiveDefault = new ProcessStartInfo();
        PythonChildEnvironment.ApplyInteractiveWorkerThreadCaps(interactiveDefault, processorCountOverride: 12);
        Assert.Equal("4", interactiveDefault.Environment[PythonChildEnvironment.PolarsMaxThreadsVariable]);

        var trainer = new ProcessStartInfo();
        PythonChildEnvironment.ApplyTrainingWorkerThreadCaps(trainer, processorCountOverride: 12);
        Assert.Equal("8", trainer.Environment[PythonChildEnvironment.OmpNumThreadsVariable]);
        Assert.Equal("8", trainer.Environment[PythonChildEnvironment.OpenBlasNumThreadsVariable]);
        Assert.Equal("8", trainer.Environment[PythonChildEnvironment.MklNumThreadsVariable]);
        Assert.Equal("8", trainer.Environment[PythonChildEnvironment.PolarsMaxThreadsVariable]);

        var trainerQuad = new ProcessStartInfo();
        PythonChildEnvironment.ApplyTrainingWorkerThreadCaps(trainerQuad, processorCountOverride: 4);
        Assert.Equal("2", trainerQuad.Environment[PythonChildEnvironment.OmpNumThreadsVariable]);
    }
}
