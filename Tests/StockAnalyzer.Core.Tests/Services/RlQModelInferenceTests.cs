#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Actual CPU ONNX graphs; behavior of the Q/actor tensor boundary independent of how buffers are owned.</summary>
public sealed partial class RlGenerationAndQValueTests
{
    private static float[] ExpectedOutput(bool continuous) => continuous ? new[] { .25f, -.5f } : new[] { 0f, 1f, -1f };
    private static float[] Observation() => Enumerable.Range(0, RlContract.ObservationLength).Select(i => (i % 7) * .125f - .25f).ToArray();

    [Theory] [InlineData(false)] [InlineData(true)]
    public void M03_ReturnedOutputIsIndependentlyOwnedAcrossRepeatedCalls(bool continuous)
    {
        using var model = new RlQModel(DiagnosticModel(continuous), continuous);
        var observation = Observation();
        var first = model.Infer(observation);
        Assert.Equal(ExpectedOutput(continuous), first);
        float[] snapshot = first.ToArray();
        for (int i = 0; i < first.Length; i++) first[i] = float.NaN;
        var second = model.Infer(observation);
        Assert.NotSame(first, second);
        Assert.Equal(snapshot, second);
        Assert.Equal(ExpectedOutput(continuous), second);
    }

    [Theory]
    [InlineData(false, float.NaN)] [InlineData(true, float.NaN)]
    [InlineData(false, float.PositiveInfinity)] [InlineData(true, float.NegativeInfinity)]
    public void M03_NonFiniteOrMalformedObservationsAreRejectedAndTheModelStaysUsable(bool continuous, float bad)
    {
        using var model = new RlQModel(DiagnosticModel(continuous), continuous);
        var observation = Observation(); observation[RlContract.ObservationLength / 2] = bad;
        Assert.Throws<InvalidDataException>(() => model.Infer(observation));
        Assert.Throws<InvalidDataException>(() => model.Infer(new float[RlContract.ObservationLength - 1]));
        Assert.Throws<ArgumentNullException>(() => model.Infer(null!));
        Assert.Equal(ExpectedOutput(continuous), model.Infer(Observation()));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task M03_ConcurrentCallsOnOneModelReturnTheirOwnCorrectResults(bool continuous)
    {
        using var model = new RlQModel(DiagnosticModel(continuous), continuous);
        var observation = Observation();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            Enumerable.Range(0, 50).Select(__ => model.Infer(observation)).ToArray())));
        var all = results.SelectMany(r => r).ToArray();
        Assert.Equal(8 * 50, all.Length);
        Assert.All(all, r => Assert.Equal(ExpectedOutput(continuous), r));
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void M03_InferAfterDisposeFailsAsDisposedAndDisposeIsRepeatable(bool continuous)
    {
        var model = new RlQModel(DiagnosticModel(continuous), continuous);
        Assert.Equal(ExpectedOutput(continuous), model.Infer(Observation()));
        model.Dispose();
        Assert.ThrowsAny<ObjectDisposedException>(() => model.Infer(Observation()));
        model.Dispose();
    }

    public const string MeasurementOutputVariable = "SA_RL_M03_MEASURE_OUTPUT";
    public sealed class InferenceMeasurementAttribute : FactAttribute
    { public InferenceMeasurementAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MeasurementOutputVariable))) Skip = $"Opt-in evidence run: set {MeasurementOutputVariable} to an owned output directory."; } }

    /// <summary>Opt-in evidence recorder, not a verifier: skipped runs are not executed measurements and nothing is written unless an owned directory is supplied.</summary>
    [InferenceMeasurement]
    public void M03_WarmedRepeatedInferenceAllocationIsMeasuredAndRecorded()
    {
        string output = Environment.GetEnvironmentVariable(MeasurementOutputVariable)!; Directory.CreateDirectory(output);
        var results = new[] { false, true }.Select(continuous =>
        {
            using var model = new RlQModel(DiagnosticModel(continuous), continuous);
            var observation = Observation(); float[]? last = null;
            for (int i = 0; i < RlContract.InferenceWarmupCalls; i++) last = model.Infer(observation);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < RlContract.InferenceMeasuredCalls; i++) last = model.Infer(observation);
            long managed = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(ExpectedOutput(continuous), last);
            // The returned array and runtime result wrappers are unavoidable allocations, so no zero-allocation claim is made.
            return new
            {
                kind = continuous ? "actor" : "q", warmup_calls = RlContract.InferenceWarmupCalls, measured_calls = RlContract.InferenceMeasuredCalls,
                managed_allocated_bytes = managed, bytes_per_call = managed / (double)RlContract.InferenceMeasuredCalls,
                measured_at_utc = DateTimeOffset.UtcNow,
            };
        }).ToArray();
        File.WriteAllText(Path.Combine(output, MeasurementFileName), JsonSerializer.Serialize(results));
    }
    private const string MeasurementFileName = "rl_infer_allocation_measurement.json";
}
