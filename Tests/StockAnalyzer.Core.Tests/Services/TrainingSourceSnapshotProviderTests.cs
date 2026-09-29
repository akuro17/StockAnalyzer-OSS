using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class TrainingSourceSnapshotProviderTests
{
    [Theory]
    [InlineData(TrainingTimeframe.Weekly, 7)]
    [InlineData(TrainingTimeframe.Monthly, 30)]
    public async Task PreparedInputAndIndicators_ShareCopiedDecimalValues(TrainingTimeframe timeframe, int periodDays)
    {
        using var fixture = new Fixture(timeframe, periodDays, new[] { "AAA", "BBB" });
        var prepared = await new TrainingSourceSnapshotProvider().PrepareAsync(
            fixture.Config, fixture.RunDirectory, fixture.JobStart);
        Assert.Equal(2, prepared.Symbols.Count);
        var exporter = new IndicatorChannelExporter(new Mock<IMarketDataProvider>().Object, IndicatorFactory.Default);
        var channels = await exporter.ExportPreparedAsync(fixture.Config.FeatureSpec!, prepared);
        foreach (var (symbol, item) in prepared.Symbols)
        {
            Assert.Equal(24, item.Candles.Count);
            using var db = new DuckDBConnection("DataSource=:memory:");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = $"SELECT close, volume FROM read_parquet('{SqlPath(item.PreparedPath)}') ORDER BY date LIMIT 1";
            using (var reader = command.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal((double)item.Candles[0].Close, reader.GetDouble(0));
                Assert.Equal(item.Candles[0].Volume, reader.GetInt64(1));
            }
            command.CommandText = $"SELECT channel_1 FROM read_parquet('{SqlPath(channels[symbol])}') ORDER BY date DESC LIMIT 1";
            Assert.NotEqual(999d, Convert.ToDouble(command.ExecuteScalar()));
        }
    }

    [Theory]
    [InlineData("missing_evidence")]
    [InlineData("not_final")]
    [InlineData("future")]
    [InlineData("overlap")]
    [InlineData("hash")]
    [InlineData("traversal")]
    [InlineData("null_price")]
    [InlineData("null_volume")]
    [InlineData("nonfinite")]
    [InlineData("overflow")]
    [InlineData("wrong_schema")]
    [InlineData("date_reversal")]
    [InlineData("duplicate_date")]
    [InlineData("negative_volume")]
    [InlineData("offset_missing")]
    public async Task PreparedInput_RejectsInvalidSource(string mutation)
    {
        using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" }, mutation);
        await Assert.ThrowsAnyAsync<Exception>(() => new TrainingSourceSnapshotProvider().PrepareAsync(
            fixture.Config, fixture.RunDirectory, fixture.JobStart));
    }

    [Fact]
    public async Task PreparedInput_AcceptsNestedRelativeParquetPath()
    {
        using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" }, "custom_path");
        var result = await new TrainingSourceSnapshotProvider().PrepareAsync(
            fixture.Config, fixture.RunDirectory, fixture.JobStart);
        Assert.Single(result.Symbols);
    }

    [Fact]
    public async Task PreparedInput_AcceptsQuotedDirectoryUnderNonGregorianCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" }, "quoted_path");
            var prepared = await new TrainingSourceSnapshotProvider().PrepareAsync(
                fixture.Config, fixture.RunDirectory, fixture.JobStart);
            Assert.Single(prepared.Symbols);
            using var db = new DuckDBConnection("DataSource=:memory:");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM read_parquet('{SqlPath(prepared.Symbols["AAA"].PreparedPath)}')";
            Assert.Equal(24L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("float32")]
    [InlineData("decimal38")]
    public async Task PreparedInput_AcceptsSupportedPriceTypes(string type)
    {
        using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" }, type);
        var result = await new TrainingSourceSnapshotProvider().PrepareAsync(
            fixture.Config, fixture.RunDirectory, fixture.JobStart);
        Assert.Equal(101m, result.Symbols["AAA"].Candles[0].Close);
    }

    [Fact]
    public void WeeklyIndicatorConfig_RequiresManifest_ButDailyDoesNot()
    {
        using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" });
        Assert.Throws<InvalidOperationException>(() => (fixture.Config with { SourceManifestPath = null }).Validate());
        (fixture.Config with { Timeframe = TrainingTimeframe.Daily, SourceManifestPath = null }).Validate();
    }

    [Fact]
    public void WeeklyIndicatorConfig_RejectsMalformedFeatureSpecBeforeManifestCheck()
    {
        using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" });
        var missingChannels = fixture.Config with
        {
            FeatureSpec = new FeatureSpec { Channels = null! },
            SourceManifestPath = null,
        };
        var nullChannel = fixture.Config with
        {
            FeatureSpec = new FeatureSpec { Channels = new FeatureChannel[] { null! } },
            SourceManifestPath = null,
        };

        Assert.False(missingChannels.RequiresFinalizedSource);
        Assert.False(nullChannel.RequiresFinalizedSource);
        Assert.Contains("FeatureSpec is invalid", Assert.Throws<InvalidOperationException>(missingChannels.Validate).Message);
        Assert.Contains("FeatureSpec is invalid", Assert.Throws<InvalidOperationException>(nullChannel.Validate).Message);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.data")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public async Task PreparedInput_RejectsReservedSymbolBeforeCreatingSnapshot(string reserved)
    {
        using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" });
        var manifestPath = fixture.Config.SourceManifestPath!;
        var manifest = File.ReadAllText(manifestPath).Replace("\"symbol\":\"AAA\"", $"\"symbol\":\"{reserved}\"");
        File.WriteAllText(manifestPath, manifest);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TrainingSourceSnapshotProvider().PrepareAsync(
                fixture.Config with { Symbols = new[] { reserved } }, fixture.RunDirectory, fixture.JobStart));
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    [Fact]
    public async Task PreparedInput_CancelBeforeCopy_StopsWithoutCreatingRunFiles()
    {
        using var fixture = new Fixture(TrainingTimeframe.Weekly, 7, new[] { "AAA" });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TrainingSourceSnapshotProvider().PrepareAsync(fixture.Config, fixture.RunDirectory,
                fixture.JobStart, ct: cancelled.Token));
        Assert.False(File.Exists(Path.Combine(fixture.RunDirectory, "snapshot", "AAA.parquet")));
    }

    [Fact]
    public async Task PreparedExporter_RejectsNonCausalIndicator()
    {
        var input = new PreparedTrainingInput(Path.GetTempPath(), Path.GetTempPath(), "provider", "revision",
            DateTimeOffset.UtcNow, new Dictionary<string, PreparedTrainingSymbol>
            {
                ["AAA"] = new("hash", "source", "prepared", new[]
                {
                    new CandleData(new DateTime(2024, 1, 1), 1m, 1m, 1m, 1m, 1),
                }),
            });
        var spec = new FeatureSpec { Channels = new[]
        {
            new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.ZigZag },
        } };
        var exporter = new IndicatorChannelExporter(new Mock<IMarketDataProvider>().Object, IndicatorFactory.Default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportPreparedAsync(spec, input));
    }

    private static string SqlPath(string path) =>
        ParquetMarketDataProvider.EscapeDuckDbPath(path.Replace("\\", "/"));

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_m04_fixture_" + Guid.NewGuid().ToString("N"));
        public string RunDirectory => Path.Combine(_root, "run");
        public DateTimeOffset JobStart { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public TrainingJobConfig Config { get; }

        public Fixture(TrainingTimeframe timeframe, int days, string[] symbols, string? mutation = null)
        {
            Directory.CreateDirectory(_root);
            var entries = new List<object>();
            foreach (var symbol in symbols)
            {
                var nestedDirectory = mutation switch
                {
                    "custom_path" => "nested",
                    "quoted_path" => "provider's records",
                    _ => null,
                };
                if (nestedDirectory is not null) Directory.CreateDirectory(Path.Combine(_root, nestedDirectory));
                var path = Path.Combine(_root, nestedDirectory ?? "", symbol + ".parquet");
                var dates = Enumerable.Range(0, 24).Select(i => new DateTime(2024, 1, 1).AddDays(i * days)).ToArray();
                var rows = dates.Select((date, i) =>
                    $"(DATE '{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}', CAST({100 + i} AS DOUBLE), CAST({102 + i} AS DOUBLE), CAST({99 + i} AS DOUBLE), CAST({101 + i} AS DOUBLE), CAST(1000 AS BIGINT), 999)");
                using (var db = new DuckDBConnection("DataSource=:memory:"))
                {
                    db.Open();
                    using var command = db.CreateCommand();
                    command.CommandText = $"COPY (SELECT * FROM (VALUES {string.Join(",", rows)}) AS t(date, open, high, low, close, volume, channel_1)) TO '{SqlPath(path)}' (FORMAT PARQUET)";
                    command.ExecuteNonQuery();
                }
                if (mutation is "float32" or "decimal38" or "null_price" or "null_volume" or "nonfinite" or "overflow"
                    or "wrong_schema" or "date_reversal" or "duplicate_date" or "negative_volume")
                {
                    using var db = new DuckDBConnection("DataSource=:memory:");
                    db.Open();
                    using var command = db.CreateCommand();
                    var changedPath = Path.Combine(_root, "changed.parquet");
                    var source = SqlPath(path);
                    var changed = SqlPath(changedPath);
                    var projection = mutation switch
                    {
                        "float32" => "date, CAST(open AS FLOAT) AS open, CAST(high AS FLOAT) AS high, CAST(low AS FLOAT) AS low, CAST(close AS FLOAT) AS close, volume",
                        "decimal38" => "date, CAST(open AS DECIMAL(38,6)) AS open, CAST(high AS DECIMAL(38,6)) AS high, CAST(low AS DECIMAL(38,6)) AS low, CAST(close AS DECIMAL(38,6)) AS close, volume",
                        "null_price" => "date, CAST(NULL AS DOUBLE) AS open, high, low, close, volume",
                        "null_volume" => "date, open, high, low, close, CAST(NULL AS BIGINT) AS volume",
                        "nonfinite" => "date, CAST('NaN' AS DOUBLE) AS open, high, low, close, volume",
                        "overflow" => "date, CAST(1e100 AS DOUBLE) AS open, high, low, close, volume",
                        "wrong_schema" => "date, CAST(open AS BIGINT) AS open, high, low, close, volume",
                        "duplicate_date" => "DATE '2024-01-01' AS date, open, high, low, close, volume",
                        "negative_volume" => "date, open, high, low, close, CAST(-1 AS BIGINT) AS volume",
                        _ => "date, open, high, low, close, volume",
                    };
                    var ordering = mutation == "date_reversal" ? " ORDER BY date DESC" : "";
                    command.CommandText = $"COPY (SELECT {projection} FROM read_parquet('{source}'){ordering}) TO '{changed}' (FORMAT PARQUET)";
                    command.ExecuteNonQuery();
                    File.Move(changedPath, path, true);
                }
                var evidence = dates.Select((date, i) => new
                {
                    date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    period_start_utc = new DateTimeOffset(date, TimeSpan.Zero),
                    period_end_utc = new DateTimeOffset(date.AddDays(days), TimeSpan.Zero),
                    available_at_utc = new DateTimeOffset(date.AddDays(days + 1), TimeSpan.Zero),
                    is_final = mutation != "not_final" || i != 0,
                }).Cast<object>().ToList();
                if (mutation == "missing_evidence") evidence.RemoveAt(0);
                if (mutation == "future") evidence[0] = new { date = dates[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), period_start_utc = JobStart.AddDays(1), period_end_utc = JobStart.AddDays(2), available_at_utc = JobStart.AddDays(3), is_final = true };
                if (mutation == "overlap") evidence[1] = new { date = dates[1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), period_start_utc = new DateTimeOffset(dates[0].AddDays(1), TimeSpan.Zero), period_end_utc = new DateTimeOffset(dates[1].AddDays(days), TimeSpan.Zero), available_at_utc = new DateTimeOffset(dates[1].AddDays(days + 1), TimeSpan.Zero), is_final = true };
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                entries.Add(new { symbol, path = mutation == "traversal" ? "../outside.parquet"
                        : nestedDirectory is not null ? nestedDirectory + "/" + symbol + ".parquet" : symbol + ".parquet",
                    sha256 = mutation == "hash" ? new string('0', 64) : hash,
                    row_count = dates.Length, bar_evidence = evidence });
            }
            var manifestPath = Path.Combine(_root, "manifest.json");
            var manifestJson = JsonSerializer.Serialize(new
            {
                schema_version = 1, provider_id = "fixture", dataset_revision = "rev-1",
                as_of_utc = JobStart.AddDays(-1), timeframe = timeframe.ToString().ToLowerInvariant(), symbols = entries,
            });
            if (mutation == "offset_missing") manifestJson = manifestJson.Replace("+00:00", "");
            File.WriteAllText(manifestPath, manifestJson);
            Config = new TrainingJobConfig
            {
                Symbols = symbols, Architecture = "lstm", WindowSize = 5, Horizon = 1,
                Timeframe = timeframe, FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec { Channels = new[]
                {
                    new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close },
                    new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA },
                } },
                SourceManifestPath = manifestPath,
            };
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
