using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// One prepared run supplies the C# decimal candles, registry-calculated indicator exports,
/// Python pre-fit tensors and C# inference windows. No indicator series is fabricated in Python.
/// </summary>
public sealed class PreparedSourcePipelineParityTests
{
    private const int Window = 6;
    private const int Horizon = 2;
    private const int Splits = 3;
    private const int SourceRows = 90;
    private const int EqualityAnchorRow = 20;
    private const int FutureMutationRow = EqualityAnchorRow + 1;
    private const decimal Threshold = 0.01m;
    private const int SmaPeriod = 4;
    private const int EmaPeriod = 6;

    [Theory]
    [InlineData(TrainingTimeframe.Weekly, 7)]
    [InlineData(TrainingTimeframe.Monthly, 30)]
    public async Task PreparedSource_ProducesCausalPythonAndInferenceParity(
        TrainingTimeframe timeframe, int periodDays)
    {
        using var baselineFixture = new PipelineFixture(timeframe, periodDays, mutateFuture: false);
        using var changedFixture = new PipelineFixture(timeframe, periodDays, mutateFuture: true);
        var baseline = await PrepareAndInspectAsync(baselineFixture);
        var changed = await PrepareAndInspectAsync(changedFixture);

        AssertPipeline(baselineFixture, baseline);
        AssertPipeline(changedFixture, changed);

        var before = baseline.Report.Symbols["AAA"];
        var after = changed.Report.Symbols["AAA"];
        AssertTensorEqual(
            before.Tensors[Array.IndexOf(before.Anchors, EqualityAnchorRow)],
            after.Tensors[Array.IndexOf(after.Anchors, EqualityAnchorRow)]);
        var lateAnchor = FutureMutationRow + Window - 1;
        var lateBefore = before.Tensors[Array.IndexOf(before.Anchors, lateAnchor)];
        var lateAfter = after.Tensors[Array.IndexOf(after.Anchors, lateAnchor)];
        Assert.True(HasDifferentValue(lateBefore, lateAfter),
            "Future mutation must affect a later window but cannot change the earlier one.");
        Assert.Equal(before.Tensors.Length, after.Tensors.Length);
        for (var i = 0; i < before.Tensors.Length; i++)
            AssertTensorEqual(baseline.Report.Symbols["BBB"].Tensors[i],
                changed.Report.Symbols["BBB"].Tensors[i]);
    }

    private static async Task<PipelineRun> PrepareAndInspectAsync(PipelineFixture fixture)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
            throw SkipException.ForSkip("The training Python virtual environment is not installed.");

        var prepared = await new TrainingSourceSnapshotProvider().PrepareAsync(
            fixture.Config, fixture.RunDirectory, fixture.JobStart);
        var exporter = new IndicatorChannelExporter(new Mock<IMarketDataProvider>().Object,
            IndicatorFactory.Default);
        var exports = await exporter.ExportPreparedAsync(fixture.Config.FeatureSpec!, prepared);
        var config = fixture.Config with
        {
            PreparedInputDir = prepared.DatasetDirectory,
            IndicatorChannelExportPaths = exports,
        };
        var configPath = Path.Combine(fixture.RunDirectory, "pipeline_job.json");
        var reportPath = Path.Combine(fixture.RunDirectory, "pipeline_report.json");
        File.WriteAllText(configPath, TrainingConfigJson.Serialize(config), TrainingConfigJson.Utf8NoBom);
        var script = Path.Combine(root.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets",
            "check_prepared_pipeline_parity.py");
        var start = new ProcessStartInfo(python) { WorkingDirectory = root.FullName };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(configPath);
        start.ArgumentList.Add(reportPath);
        start.ArgumentList.Add(Threshold.ToString(CultureInfo.InvariantCulture));
        var output = await TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromMinutes(2));
        Assert.Contains("prepared pipeline parity report: PASS", output);
        var report = JsonSerializer.Deserialize<PipelineReport>(File.ReadAllText(reportPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.NotNull(report);
        return new PipelineRun(prepared, exports, report);
    }

    private static void AssertPipeline(PipelineFixture fixture, PipelineRun run)
    {
        var report = run.Report;
        var spec = fixture.Config.FeatureSpec!;
        Assert.Equal(new[] { "AAA", "BBB" }, report.SymbolOrder);
        Assert.Equal(SourceRows, report.FullRowCount["AAA"]);
        Assert.Equal(SourceRows, report.FullRowCount["BBB"]);
        Assert.Equal(spec.Channels.Count, report.FitTrain[0][0].Length);
        Assert.Equal(Window, report.FitTrain[0].Length);
        Assert.Equal(report.FitTrain.Length, report.FitTrainLabels.Length);
        Assert.Equal(report.FitValidation.Length, report.FitValidationLabels.Length);

        var expectedTrain = new List<float[][]>();
        var expectedValidation = new List<float[][]>();
        var expectedTrainLabels = new List<int>();
        var expectedValidationLabels = new List<int>();
        var trainAnchorDates = new List<string>();
        var validationAnchorDates = new List<string>();
        foreach (var symbol in report.SymbolOrder)
        {
            var source = run.Prepared.Symbols[symbol];
            var item = report.Symbols[symbol];
            Assert.Equal(SourceRows, source.Candles.Count);
            Assert.Equal(source.SourceSha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source.SnapshotPath))));
            Assert.Equal(SourceRows, item.FullDates.Length);
            Assert.True(item.TrainingDates.Length < item.FullDates.Length);
            Assert.Equal(item.TrainingDates.Length, item.Projection.Length);
            var cutoff = DateOnly.Parse(item.FullDates[^1], CultureInfo.InvariantCulture)
                .AddDays(-fixture.Config.OosTailDays!.Value);
            Assert.All(item.TrainingDates,
                date => Assert.True(DateOnly.Parse(date, CultureInfo.InvariantCulture) <= cutoff));
            Assert.Contains(item.FullDates,
                date => DateOnly.Parse(date, CultureInfo.InvariantCulture) > cutoff);

            for (var row = 0; row < item.TrainingDates.Length; row++)
            {
                var candle = source.Candles[row];
                Assert.Equal(DateOnly.FromDateTime(candle.Timestamp),
                    DateOnly.Parse(item.TrainingDates[row], CultureInfo.InvariantCulture));
                Assert.Equal((double)candle.Open, item.Projection[row][0]);
                Assert.Equal((double)candle.High, item.Projection[row][1]);
                Assert.Equal((double)candle.Low, item.Projection[row][2]);
                Assert.Equal((double)candle.Close, item.Projection[row][3]);
                Assert.Equal((double)candle.Volume, item.Projection[row][4]);
            }

            Assert.Equal(item.Anchors.Length, item.Tensors.Length);
            Assert.Equal(item.Anchors.Length, item.Labels.Length);
            Assert.Equal(item.Anchors.Length, item.RegressionLabels.Length);
            Assert.True(item.Anchors.Length < item.TrainingDates.Length - Window - Horizon + 1,
                "Indicator warmup must drop some candidate windows.");
            Assert.Equal(EmaPeriod - 1 + Window - 1, item.Anchors[0]);
            Assert.True(item.Anchors[^1] + Horizon < item.TrainingDates.Length);

            for (var sample = 0; sample < item.Anchors.Length; sample++)
            {
                var anchor = item.Anchors[sample];
                var actual = item.Tensors[sample];
                Assert.Equal(Window, actual.Length);
                var inference = new float[Window * spec.Channels.Count];
                PredictionService.BuildComposedTensor(source.Candles, anchor - Window + 1,
                    Window, spec, IndicatorFactory.Default, inference);
                for (var bar = 0; bar < Window; bar++)
                {
                    Assert.Equal(spec.Channels.Count, actual[bar].Length);
                    for (var channel = 0; channel < spec.Channels.Count; channel++)
                        AssertClose(inference[bar * spec.Channels.Count + channel],
                            actual[bar][channel], $"{symbol} anchor={anchor} bar={bar} channel={channel}");
                    AssertClose((float)(double)source.Candles[anchor - Window + 1 + bar].Open,
                        actual[bar][3], $"{symbol} raw Open channel");
                }

                var anchorClose = source.Candles[anchor].Close;
                var futureClose = source.Candles[anchor + Horizon].Close;
                var ratio = (futureClose - anchorClose) / anchorClose;
                var expectedLabel = ratio > Threshold ? 0 : ratio < -Threshold ? 1 : 2;
                Assert.Equal(expectedLabel, item.Labels[sample]);
                Assert.InRange(Math.Abs(item.RegressionLabels[sample] -
                    (float)Math.Log((double)futureClose / (double)anchorClose)), 0f,
                    MLDataProcessorParityTests.ParityAtol);
            }

            var lastTrainAnchor = item.Anchors[item.TrainIndices[^1]];
            var firstValidationAnchor = item.Anchors[item.ValidationIndices[0]];
            Assert.True(lastTrainAnchor + Horizon < firstValidationAnchor - Window + 1,
                "Default gap must separate each last training label from the first validation input.");
            var lastSmallTrainAnchor = item.Anchors[item.SmallTrainIndices[^1]];
            var firstSmallValidationAnchor = item.Anchors[item.SmallValidationIndices[0]];
            Assert.True(lastSmallTrainAnchor + Horizon >= firstSmallValidationAnchor - Window + 1,
                "An explicit smaller gap is accepted and can leave overlap.");
            Assert.True(report.SmallTrainCount > 0 && report.SmallValidationCount > 0);

            foreach (var index in item.TrainIndices)
            {
                expectedTrain.Add(item.Tensors[index]);
                expectedTrainLabels.Add(item.Labels[index]);
                trainAnchorDates.Add(item.TrainingDates[item.Anchors[index]]);
            }
            foreach (var index in item.ValidationIndices)
            {
                expectedValidation.Add(item.Tensors[index]);
                expectedValidationLabels.Add(item.Labels[index]);
                validationAnchorDates.Add(item.TrainingDates[item.Anchors[index]]);
            }
        }

        AssertTensorBatchEqual(expectedTrain, report.FitTrain);
        AssertTensorBatchEqual(expectedValidation, report.FitValidation);
        Assert.Equal(expectedTrainLabels, report.FitTrainLabels);
        Assert.Equal(expectedValidationLabels, report.FitValidationLabels);
        Assert.Equal(trainAnchorDates.Min(), report.DateRanges["training_start"]);
        Assert.Equal(trainAnchorDates.Max(), report.DateRanges["training_end"]);
        Assert.Equal(validationAnchorDates.Min(), report.DateRanges["validation_start"]);
        Assert.Equal(validationAnchorDates.Max(), report.DateRanges["validation_end"]);
        AssertIndicatorExport(run, "AAA");

        var aaa = report.Symbols["AAA"];
        var varyingWindow = aaa.Tensors[0];
        AssertClose(0f, varyingWindow.Min(bar => bar[1]), "nonflat Close min/max minimum");
        AssertClose(1f, varyingWindow.Max(bar => bar[1]), "nonflat Close min/max maximum");
        var zMean = varyingWindow.Average(bar => bar[2]);
        var zVariance = varyingWindow.Average(bar => Math.Pow(bar[2] - zMean, 2));
        AssertClose(0f, (float)zMean, "nonflat EMA z-score mean");
        AssertClose(1f, (float)Math.Sqrt(zVariance), "nonflat EMA z-score population sigma");
        Assert.Contains(EqualityAnchorRow, aaa.Anchors);
        Assert.Contains(0, aaa.Labels);
        Assert.Contains(1, aaa.Labels);
        Assert.Equal(2, aaa.Labels[Array.IndexOf(aaa.Anchors, EqualityAnchorRow)]);
        Assert.Equal(Threshold,
            (run.Prepared.Symbols["AAA"].Candles[EqualityAnchorRow + Horizon].Close -
             run.Prepared.Symbols["AAA"].Candles[EqualityAnchorRow].Close) /
            run.Prepared.Symbols["AAA"].Candles[EqualityAnchorRow].Close);

        var flat = report.Symbols["BBB"];
        Assert.All(flat.Labels, label => Assert.Equal(2, label));
        foreach (var tensor in flat.Tensors)
        {
            foreach (var bar in tensor)
            {
                AssertClose(100f, bar[0], "flat SMA");
                AssertClose(0.5f, bar[1], "flat Close min/max");
                AssertClose(0f, bar[2], "flat EMA z-score");
                AssertClose(100f, bar[3], "flat Open");
            }
        }
    }

    private static void AssertIndicatorExport(PipelineRun run, string symbol)
    {
        using var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        var escaped = ParquetMarketDataProvider.EscapeDuckDbPath(
            run.Exports[symbol].Replace("\\", "/"));
        command.CommandText = $"SELECT channel_0, channel_2 FROM read_parquet('{escaped}') ORDER BY date";
        using var reader = command.ExecuteReader();
        var sma = new List<double?>();
        var ema = new List<double?>();
        while (reader.Read())
        {
            sma.Add(reader.IsDBNull(0) ? null : reader.GetDouble(0));
            ema.Add(reader.IsDBNull(1) ? null : reader.GetDouble(1));
        }
        Assert.Equal(SmaPeriod - 1, sma.FindIndex(value => value.HasValue));
        Assert.Equal(EmaPeriod - 1, ema.FindIndex(value => value.HasValue));
        var closes = run.Prepared.Symbols[symbol].Candles;
        var expectedSma = closes.Take(SmaPeriod).Sum(candle => candle.Close) / SmaPeriod;
        AssertClose((float)(double)expectedSma, (float)sma[SmaPeriod - 1]!,
            "nondefault SMA parameter and price-native unit");
    }

    private static void AssertTensorBatchEqual(IReadOnlyList<float[][]> expected, float[][][] actual)
    {
        Assert.Equal(expected.Count, actual.Length);
        for (var i = 0; i < expected.Count; i++) AssertTensorEqual(expected[i], actual[i]);
    }

    private static void AssertTensorEqual(float[][] expected, float[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var bar = 0; bar < expected.Length; bar++)
        {
            Assert.Equal(expected[bar].Length, actual[bar].Length);
            for (var channel = 0; channel < expected[bar].Length; channel++)
                AssertClose(expected[bar][channel], actual[bar][channel],
                    $"bar={bar} channel={channel}");
        }
    }

    private static bool HasDifferentValue(float[][] left, float[][] right) =>
        left.SelectMany(row => row).Zip(right.SelectMany(row => row),
            (a, b) => MathF.Abs(a - b)).Any(delta => delta > MLDataProcessorParityTests.ParityAtol);

    private static void AssertClose(float expected, float actual, string label) =>
        Assert.True(MathF.Abs(expected - actual) <= MLDataProcessorParityTests.ParityAtol,
            $"{label}: expected {expected:R}, actual {actual:R}, " +
            $"allowed {MLDataProcessorParityTests.ParityAtol:R}");

    private sealed record PipelineRun(PreparedTrainingInput Prepared,
        IReadOnlyDictionary<string, string> Exports, PipelineReport Report);

    public sealed record PipelineReport
    {
        public required string[] SymbolOrder { get; init; }
        public required Dictionary<string, PipelineSymbolReport> Symbols { get; init; }
        public required float[][][] FitTrain { get; init; }
        public required float[][][] FitValidation { get; init; }
        public required int[] FitTrainLabels { get; init; }
        public required int[] FitValidationLabels { get; init; }
        public required int SmallTrainCount { get; init; }
        public required int SmallValidationCount { get; init; }
        public required Dictionary<string, string> DateRanges { get; init; }
        public required Dictionary<string, int> FullRowCount { get; init; }
    }

    public sealed record PipelineSymbolReport
    {
        public required string[] FullDates { get; init; }
        public required string[] TrainingDates { get; init; }
        public required double[][] Projection { get; init; }
        public required int[] Anchors { get; init; }
        public required int[] Labels { get; init; }
        public required float[] RegressionLabels { get; init; }
        public required float[][][] Tensors { get; init; }
        public required int[] TrainIndices { get; init; }
        public required int[] ValidationIndices { get; init; }
        public required int[] SmallTrainIndices { get; init; }
        public required int[] SmallValidationIndices { get; init; }
    }

    private sealed class PipelineFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "sa_i01_pipeline_" + Guid.NewGuid().ToString("N"));
        public string RunDirectory => Path.Combine(_root, "run");
        public DateTimeOffset JobStart { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public TrainingJobConfig Config { get; }

        public PipelineFixture(TrainingTimeframe timeframe, int periodDays, bool mutateFuture)
        {
            Directory.CreateDirectory(_root);
            var entries = new List<object>();
            foreach (var symbol in new[] { "AAA", "BBB" })
            {
                var dates = Enumerable.Range(0, SourceRows)
                    .Select(index => new DateTime(2014, 1, 1).AddDays(index * periodDays)).ToArray();
                var rows = new List<string>(SourceRows);
                for (var index = 0; index < SourceRows; index++)
                {
                    var close = symbol == "BBB" ? 100m : CloseAt(index, mutateFuture);
                    var open = symbol == "BBB" ? close : close - 0.125m;
                    var high = symbol == "BBB" ? close : close + 0.5m;
                    var low = symbol == "BBB" ? close : open - 0.5m;
                    var date = dates[index].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    rows.Add($"(DATE '{date}', {SqlDecimal(open)}, {SqlDecimal(high)}, " +
                        $"{SqlDecimal(low)}, {SqlDecimal(close)}, CAST({1000 + index} AS BIGINT))");
                }
                var path = Path.Combine(_root, symbol + ".parquet");
                using (var db = new DuckDBConnection("DataSource=:memory:"))
                {
                    db.Open();
                    using var command = db.CreateCommand();
                    var escaped = ParquetMarketDataProvider.EscapeDuckDbPath(path.Replace("\\", "/"));
                    command.CommandText = $"COPY (SELECT * FROM (VALUES {string.Join(",", rows)}) " +
                        $"AS t(date, open, high, low, close, volume)) TO '{escaped}' (FORMAT PARQUET)";
                    command.ExecuteNonQuery();
                }
                var evidence = dates.Select(date => new
                {
                    date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    period_start_utc = new DateTimeOffset(date, TimeSpan.Zero),
                    period_end_utc = new DateTimeOffset(date.AddDays(periodDays), TimeSpan.Zero),
                    available_at_utc = new DateTimeOffset(date.AddDays(periodDays + 1), TimeSpan.Zero),
                    is_final = true,
                }).ToArray();
                entries.Add(new
                {
                    symbol, path = symbol + ".parquet",
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    row_count = dates.Length, bar_evidence = evidence,
                });
            }
            var manifestPath = Path.Combine(_root, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                schema_version = 1, provider_id = "i01-fixture", dataset_revision = "v1",
                as_of_utc = JobStart.AddDays(-1), timeframe = timeframe.ToString().ToLowerInvariant(),
                symbols = entries,
            }));
            Config = new TrainingJobConfig
            {
                Symbols = new[] { "AAA", "BBB" }, Architecture = "lstm",
                Framework = TrainingFramework.PyTorch,
                WindowSize = Window, Horizon = Horizon, NSplits = Splits,
                OosTailDays = periodDays * 3,
                Timeframe = timeframe, FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec { Channels = new FeatureChannel[]
                {
                    new() { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA,
                        Params = new Dictionary<string, string> { ["Period"] = SmaPeriod.ToString(CultureInfo.InvariantCulture) } },
                    new() { Kind = FeatureChannelKind.Price, Price = PriceType.Close,
                        Normalization = ChannelNormalization.WindowMinMax },
                    new() { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.EMA,
                        Params = new Dictionary<string, string> { ["Period"] = EmaPeriod.ToString(CultureInfo.InvariantCulture) },
                        Normalization = ChannelNormalization.WindowZScore },
                    new() { Kind = FeatureChannelKind.Price, Price = PriceType.Open },
                } },
                SourceManifestPath = manifestPath,
            };
        }

        private static decimal CloseAt(int index, bool mutateFuture)
        {
            if (index == EqualityAnchorRow) return 100m;
            if (index == EqualityAnchorRow + Horizon) return 101m;
            if (index == 40) return 100m;
            if (index == 40 + Horizon) return 102m;
            var baseClose = 100m + ((index % 11) - 5) * 0.25m;
            return mutateFuture && index == FutureMutationRow ? baseClose + 10m : baseClose;
        }

        private static string SqlDecimal(decimal value) =>
            $"CAST({value.ToString(CultureInfo.InvariantCulture)} AS DECIMAL(18,4))";

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
