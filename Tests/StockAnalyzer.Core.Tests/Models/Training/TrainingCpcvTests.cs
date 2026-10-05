#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// T14 CPCV on the C# side: the counting / enumeration contract (verified against vectors written by the production Python code,
/// <c>Tests/CpcvParity.json</c>) and the job-config mirror. Only combinations are counted: no path notion exists (13-17=B).
/// </summary>
public sealed class TrainingCpcvTests
{
    private static JsonDocument Parity()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "Tests", "CpcvParity.json")));
    }

    private static TrainingJobConfig Job(Func<TrainingJobConfig, TrainingJobConfig>? change = null)
    {
        var config = new TrainingJobConfig { Symbols = new[] { "AAA" }, Architecture = "lstm", WindowSize = 20, Horizon = 5 };
        return change is null ? config : change(config);
    }

    [Fact]
    public void Constants_MatchPython()
    {
        using var parity = Parity();
        var root = parity.RootElement;
        Assert.Equal(TrainingCpcvContract.DefaultGroups, root.GetProperty("default_groups").GetInt32());
        Assert.Equal(TrainingCpcvContract.DefaultTestGroups, root.GetProperty("default_test_groups").GetInt32());
        Assert.Equal(TrainingCpcvContract.MaxGroups, root.GetProperty("max_groups").GetInt32());
        Assert.Equal(TrainingCpcvContract.MaxCombinations, root.GetProperty("max_combinations").GetInt32());
        Assert.Equal(15, TrainingCpcvContract.CombinationCount(TrainingCpcvContract.DefaultGroups, TrainingCpcvContract.DefaultTestGroups));
    }

    [Fact]
    public void CombinationCount_AgreesWithPythonOnEveryShape_AcceptedAndRejected()
    {
        using var parity = Parity();
        var accepted = parity.RootElement.GetProperty("accepted").EnumerateArray().ToArray();
        var rejected = parity.RootElement.GetProperty("rejected").EnumerateArray().ToArray();
        Assert.NotEmpty(accepted);
        Assert.NotEmpty(rejected);
        foreach (var item in accepted)
        {
            var groups = item.GetProperty("groups").GetInt32();
            var testGroups = item.GetProperty("test_groups").GetInt32();
            Assert.True(TrainingCpcvContract.TryCountCombinations(groups, testGroups, out var count, out _), $"{groups},{testGroups}");
            Assert.Equal(item.GetProperty("count").GetInt32(), count);
        }

        foreach (var item in rejected)
        {
            var groups = item.GetProperty("groups").GetInt32();
            var testGroups = item.GetProperty("test_groups").GetInt32();
            Assert.False(TrainingCpcvContract.TryCountCombinations(groups, testGroups, out var count, out var error), $"{groups},{testGroups}");
            Assert.Equal(0, count);
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue, int.MaxValue - 1)]
    [InlineData(int.MaxValue, 1_000_000)]
    [InlineData(int.MinValue, -5)]
    [InlineData(20, 10)]
    [InlineData(21, 10)]
    [InlineData(10, 3)]
    [InlineData(6, 0)]
    [InlineData(6, 6)]
    [InlineData(6, -1)]
    [InlineData(1, 1)]
    public void CombinationCount_RejectsOversizedAndMalformedRequestsWithoutOverflow(int groups, int testGroups)
    {
        Assert.False(TrainingCpcvContract.TryCountCombinations(groups, testGroups, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        var thrown = Assert.Throws<InvalidOperationException>(() => TrainingCpcvContract.CombinationCount(groups, testGroups));
        Assert.StartsWith("TrainingJobConfig: ", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapBoundary_AllowsTheLargestCandidateAndRejectsTheNextShape()
    {
        Assert.Equal(TrainingCpcvContract.MaxCombinations, TrainingCpcvContract.CombinationCount(10, 2)); // C(10,2) = 45
        Assert.Equal(55, ComputeChoose(11, 2));
        Assert.Throws<InvalidOperationException>(() => TrainingCpcvContract.CombinationCount(11, 2));
    }

    private static long ComputeChoose(int n, int k)
    {
        long value = 1;
        for (var i = 1; i <= k; i++) value = value * (n - k + i) / i;
        return value;
    }

    [Fact]
    public void Combination_EnumeratesExactlyLikeThePythonTrainer()
    {
        using var parity = Parity();
        foreach (var item in parity.RootElement.GetProperty("enumerations").EnumerateArray())
        {
            var groups = item.GetProperty("groups").GetInt32();
            var testGroups = item.GetProperty("test_groups").GetInt32();
            var expected = item.GetProperty("combinations").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            Assert.Equal(TrainingCpcvContract.CombinationCount(groups, testGroups), expected.Length);
            for (var index = 0; index < expected.Length; index++)
                Assert.Equal(expected[index], TrainingCpcvContract.Combination(groups, testGroups, index));

            // every group is held out equally often: C(N-1, k-1) times
            var perGroup = Enumerable.Range(0, groups).Select(g => expected.Count(c => c.Contains(g))).ToArray();
            Assert.All(perGroup, count => Assert.Equal(ComputeChoose(groups - 1, testGroups - 1), count));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    [InlineData(int.MaxValue)]
    public void Combination_RejectsAnIndexOutsideTheCount(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingCpcvContract.Combination(6, 2, index));

    // ------------------------------------------------------------------------------- config mirror

    [Fact]
    public void Config_DefaultsAreOff_NotWritten_AndTheWireKeysRoundTrip()
    {
        // an unused CPCV request leaves no key on the wire, so an older training package (which rejects unknown keys) still reads the job
        var plain = JsonDocument.Parse(TrainingConfigJson.Serialize(Job())).RootElement;
        Assert.False(plain.TryGetProperty("cpcv", out _));
        Assert.False(plain.TryGetProperty("cpcv_groups", out _));
        Assert.False(plain.TryGetProperty("cpcv_test_groups", out _));
        Job().Validate();
        var defaulted = TrainingConfigJson.DeserializeConfig(TrainingConfigJson.Serialize(Job()));
        Assert.False(defaulted.Cpcv);
        Assert.Equal((TrainingCpcvContract.DefaultGroups, TrainingCpcvContract.DefaultTestGroups),
            (defaulted.CpcvGroups, defaulted.CpcvTestGroups));

        var cpcv = Job(c => c with { Cpcv = true, CpcvGroups = 5, CpcvTestGroups = 1 });
        cpcv.Validate();
        var wire = TrainingConfigJson.Serialize(cpcv);
        Assert.Contains("\"cpcv\": true", wire);
        Assert.Contains("\"cpcv_groups\": 5", wire);
        Assert.Contains("\"cpcv_test_groups\": 1", wire);
        var back = TrainingConfigJson.DeserializeConfig(wire);
        Assert.True(back.Cpcv);
        Assert.Equal((5, 1), (back.CpcvGroups, back.CpcvTestGroups));

        // the shape keys are written only while they differ from the shared default
        var onlyTestGroups = JsonDocument.Parse(TrainingConfigJson.Serialize(
            Job(c => c with { Cpcv = true, CpcvTestGroups = 3 }))).RootElement;
        Assert.True(onlyTestGroups.GetProperty("cpcv").GetBoolean());
        Assert.False(onlyTestGroups.TryGetProperty("cpcv_groups", out _));
        Assert.Equal(3, onlyTestGroups.GetProperty("cpcv_test_groups").GetInt32());
    }

    [Fact]
    public void Config_Validate_AcceptsTheSupportedCpcvRequests()
    {
        Job(c => c with { Cpcv = true }).Validate();
        Job(c => c with { Cpcv = true, CpcvGroups = 10, CpcvTestGroups = 2 }).Validate();
        Job(c => c with { Cpcv = true, TargetType = TargetType.Regression }).Validate();
        Job(c => c with { Cpcv = true, Calibration = true }).Validate();
        Job(c => c with { Cpcv = true, Pretraining = TrainingPretraining.MaskedReconstruction }).Validate();
        // the CPCV shape is only checked while CPCV is requested
        Job(c => c with { Cpcv = false, CpcvGroups = 99, CpcvTestGroups = 98 }).Validate();
    }

    public static IEnumerable<object[]> InvalidCpcvConfigs()
    {
        static object[] Case(Func<TrainingJobConfig, TrainingJobConfig> change) => new object[] { change };
        yield return Case(c => c with { Cpcv = true, CpcvGroups = 10, CpcvTestGroups = 3 });
        yield return Case(c => c with { Cpcv = true, CpcvGroups = 6, CpcvTestGroups = 0 });
        yield return Case(c => c with { Cpcv = true, CpcvGroups = 6, CpcvTestGroups = 6 });
        yield return Case(c => c with { Cpcv = true, CpcvGroups = 21, CpcvTestGroups = 1 });
        yield return Case(c => c with { Cpcv = true, CpcvGroups = int.MaxValue, CpcvTestGroups = 1 });
        yield return Case(c => c with { Cpcv = true, EnsembleMethod = TrainingEnsembleMethod.Bagging });
        yield return Case(c => c with
        {
            Cpcv = true, InitializationMode = TrainingInitializationMode.Resume, CheckpointPath = "run.json",
        });
    }

    [Theory]
    [MemberData(nameof(InvalidCpcvConfigs))]
    public void Config_Validate_RejectsEveryUnsupportedCpcvRequest(Func<TrainingJobConfig, TrainingJobConfig> change)
    {
        var error = Assert.Throws<InvalidOperationException>(() => change(Job()).Validate());
        Assert.StartsWith("TrainingJobConfig: ", error.Message, StringComparison.Ordinal);
    }
}
