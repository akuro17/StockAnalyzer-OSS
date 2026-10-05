#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A producer-shaped source generated from the Attestation Manifest v2 contract: a real DuckDB-written Parquet with the real
/// producer's column types (date DATE, float32 prices, BIGINT volume) and a sidecar manifest. No real provider emits these files.
/// </summary>
internal sealed class AttestedSourceFixture : IDisposable
{
    public const string Instrument = "INST";
    private readonly DuckDBConnectionManager _db = new();
    private readonly MarketDataSettings _market;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sa_fp06_" + Guid.NewGuid().ToString("N"));
    public ParquetDataService Service { get; }
    /// <summary>Where the loader makes its private Parquet copies (inside the fixture root, so nothing leaks into the real Data folder).</summary>
    public string CopyRoot => Path.Combine(Root, "attested-copies");

    public AttestedSourceFixture(TimeProvider clock, long maxManifestBytes = 67108864)
    {
        _market = new MarketDataSettings { DailyDataPath = Path.Combine(Root, "Daily"), WeeklyDataPath = Path.Combine(Root, "Weekly"), MonthlyDataPath = Path.Combine(Root, "Monthly") };
        Service = new ParquetDataService(_db, Options.Create(_market), timeProvider: clock,
            monitoringOptions: Options.Create(new PredictionMonitoringSettings { AttestedManifestMaxBytes = maxManifestBytes }), attestedCopyRoot: CopyRoot);
    }

    public string Folder(TimeFrame timeframe) => timeframe switch
    {
        TimeFrame.W1 => _market.WeeklyDataPath!, TimeFrame.MN1 => _market.MonthlyDataPath!, _ => _market.DailyDataPath!,
    };

    /// <summary>Writes the Parquet with FLOAT prices like the real updater and returns the SHA-256 of the file bytes.</summary>
    public string WriteParquet(IReadOnlyList<CandleData> bars, string fileName = Instrument + ".parquet", TimeFrame timeframe = TimeFrame.D1)
    {
        Directory.CreateDirectory(Folder(timeframe));
        var path = Path.Combine(Folder(timeframe), fileName);
        TestParquetWriter.Write(_db, path, bars, floatPrices: true);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    public static Dictionary<string, object?> Manifest(IReadOnlyList<CandleData> bars, string sha, DateTimeOffset asOf, string timeframe = "daily",
        string instrument = Instrument, long epoch = 0, ulong sequence = 1, string finality = "final")
    {
        static string Z(DateTimeOffset v) => v.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return new Dictionary<string, object?>
        {
            ["schema_version"] = 2, ["provider_id"] = "prov", ["instrument_id"] = instrument, ["timeframe"] = timeframe,
            ["revision_epoch"] = epoch, ["provider_sequence"] = sequence, ["as_of_utc"] = Z(asOf),
            ["source"] = new Dictionary<string, object?> { ["path"] = instrument + ".parquet", ["sha256"] = sha, ["row_count"] = bars.Count },
            ["bar_evidence"] = bars.Select(c =>
            {
                var start = PredictionLogService.Utc(c.Timestamp);
                return new Dictionary<string, object?>
                {
                    ["date"] = c.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["period_start_utc"] = Z(start),
                    ["period_end_utc"] = Z(start.AddDays(1)), ["available_at_utc"] = Z(start.AddDays(1)), ["finality"] = finality,
                };
            }).ToList(),
            ["tombstones"] = new List<Dictionary<string, object?>>(),
        };
    }

    public void WriteManifest(Dictionary<string, object?> manifest, string instrument = Instrument, TimeFrame timeframe = TimeFrame.D1) =>
        File.WriteAllText(Path.Combine(Folder(timeframe), instrument + AttestationManifestReader.FileSuffix), JsonSerializer.Serialize(manifest));

    public void Dispose() { _db.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

public sealed class AttestationManifestLoaderTests : IDisposable
{
    private readonly SqliteMonitoringTestKit _kit = new();
    private readonly AttestedSourceFixture _source;
    private readonly ImmutableArray<CandleData> _bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28);

    public AttestationManifestLoaderTests() => _source = new AttestedSourceFixture(_kit.Clock);
    public void Dispose() { _source.Dispose(); _kit.Dispose(); }

    private Dictionary<string, object?> Write(Action<Dictionary<string, object?>>? mutate = null, string finality = "final")
    {
        var sha = _source.WriteParquet(_bars);
        var manifest = AttestedSourceFixture.Manifest(_bars, sha, _kit.Clock.Now, finality: finality);
        mutate?.Invoke(manifest);
        _source.WriteManifest(manifest);
        return manifest;
    }

    private static List<Dictionary<string, object?>> Evidence(Dictionary<string, object?> m) => (List<Dictionary<string, object?>>)m["bar_evidence"]!;
    private static Dictionary<string, object?> Source(Dictionary<string, object?> m) => (Dictionary<string, object?>)m["source"]!;
    private static List<Dictionary<string, object?>> Tombstones(Dictionary<string, object?> m) => (List<Dictionary<string, object?>>)m["tombstones"]!;

    [Fact]
    public async Task ValidSidecar_LoadsAnAlignedSnapshot_FromARealFloat32Parquet()
    {
        Write(m => { m["revision_epoch"] = 3L; m["provider_sequence"] = ulong.MaxValue; Tombstones(m).Add(new() { ["anchor_utc"] = "2020-01-01T00:00:00Z", ["available_at_utc"] = "2020-01-02T00:00:00+00:00", ["reason"] = "delisted bar" }); });
        var snapshot = await _source.Service.LoadAttestedObservationAsync(AttestedSourceFixture.Instrument, TimeFrame.D1);
        Assert.NotNull(snapshot);
        Assert.Equal(("prov", "INST", TimeframeType.Daily, 3L, ulong.MaxValue), (snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe, snapshot.RevisionEpoch, snapshot.ProviderSequence));
        Assert.Equal(_bars.Length, snapshot.Candles.Length);
        Assert.Equal(_bars.Select(b => (b.Timestamp, b.Close)), snapshot.Candles.Select(c => (c.Timestamp, c.Close)));
        Assert.All(snapshot.Evidence, e => Assert.Equal(BarFinality.Final, e.Finality));
        Assert.Equal(PredictionLogService.SourceRevision(snapshot.Candles), snapshot.SourceRevision);
        Assert.Equal(_kit.Clock.Now, snapshot.ObservedUtc);
        Assert.Equal("delisted bar", snapshot.Tombstones.Single().Reason);
    }

    [Fact]
    public async Task ProducerStillHoldingBothFilesOpen_DoesNotBlockTheLoader()
    {
        Write();
        var folder = _source.Folder(TimeFrame.D1);
        var manifestPath = Path.Combine(folder, AttestedSourceFixture.Instrument + AttestationManifestReader.FileSuffix);
        var parquetPath = Path.Combine(folder, AttestedSourceFixture.Instrument + ".parquet");
        // A producer that writes in place keeps a write handle that allows readers and deleters (as its atomic replace does).
        using var producerManifest = new FileStream(manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var producerParquet = new FileStream(parquetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var snapshot = await _source.Service.LoadAttestedObservationAsync(AttestedSourceFixture.Instrument, TimeFrame.D1);
        Assert.NotNull(snapshot);
        Assert.Equal(_bars.Length, snapshot.Candles.Length);
    }

    [Fact]
    public async Task MissingSidecarUnsupportedTimeframeOrBadInstrument_AreNotAttestations()
    {
        Assert.Null(await _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.D1));
        Assert.Null(await _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.H1));
        await Assert.ThrowsAsync<ArgumentException>(() => _source.Service.LoadAttestedObservationAsync(" ", TimeFrame.D1));
        foreach (var bad in new[] { "../INST", "a/b", ".." })
            await Assert.ThrowsAsync<ArgumentException>(() => _source.Service.LoadAttestedObservationAsync(bad, TimeFrame.D1));
    }

    public static IEnumerable<object[]> Rejections()
    {
        static object[] Case(string name, Action<Dictionary<string, object?>> mutate) => [name, mutate];
        yield return Case("schema version 1", m => m["schema_version"] = 1);
        yield return Case("schema version text", m => m["schema_version"] = "2");
        yield return Case("blank provider", m => m["provider_id"] = " ");
        yield return Case("instrument mismatch", m => m["instrument_id"] = "OTHER");
        yield return Case("timeframe mismatch", m => m["timeframe"] = "weekly");
        yield return Case("timeframe unknown", m => m["timeframe"] = "hourly");
        yield return Case("negative epoch", m => m["revision_epoch"] = -1L);
        yield return Case("fractional epoch", m => m["revision_epoch"] = 1.5);
        yield return Case("negative sequence", m => m["provider_sequence"] = -1L);
        yield return Case("sequence text", m => m["provider_sequence"] = "7");
        yield return Case("as_of without offset", m => m["as_of_utc"] = "2026-10-01T00:00:00");
        yield return Case("as_of with +09:00", m => m["as_of_utc"] = "2026-10-01T09:00:00+09:00");
        yield return Case("as_of in the future", m => m["as_of_utc"] = "2026-10-02T00:00:00Z");
        yield return Case("unknown finality text", m => Evidence(m)[5]["finality"] = "maybe");
        yield return Case("finality case", m => Evidence(m)[5]["finality"] = "Final");
        yield return Case("legacy is_final", m => { Evidence(m)[5].Remove("finality"); Evidence(m)[5]["is_final"] = true; });
        yield return Case("sha mismatch", m => Source(m)["sha256"] = new string('0', 64));
        yield return Case("sha not hex", m => Source(m)["sha256"] = new string('Z', 64));
        yield return Case("path with separator", m => Source(m)["path"] = "sub/INST.parquet");
        yield return Case("path traversal", m => Source(m)["path"] = "../INST.parquet");
        yield return Case("path missing file", m => Source(m)["path"] = "NOPE.parquet");
        yield return Case("path reserved device name", m => Source(m)["path"] = "CON.parquet");
        yield return Case("path reserved device name with extension", m => Source(m)["path"] = "nul.txt");
        yield return Case("path not NFC", m => Source(m)["path"] = "e\u0301.parquet");
        yield return Case("path with a Windows separator", m => Source(m)["path"] = "sub\\x.parquet");
        yield return Case("path with a trailing period", m => Source(m)["path"] = "x.parquet.");
        yield return Case("path with a superscript device name", m => Source(m)["path"] = "COM\u00b2.parquet");
        yield return Case("as_of with a space instead of T", m => m["as_of_utc"] = "2026-10-01 00:00:00Z");
        yield return Case("as_of without seconds", m => m["as_of_utc"] = "2026-10-01T00:00Z");
        yield return Case("as_of without zero padding", m => m["as_of_utc"] = "2026-10-1T0:0:0Z");
        yield return Case("as_of with leading space", m => m["as_of_utc"] = " 2026-10-01T00:00:00Z");
        yield return Case("as_of with a lowercase z and a trailing dot", m => m["as_of_utc"] = "2026-10-01T00:00:00.z");
        yield return Case("evidence time in RFC 1123", m => Evidence(m)[7]["period_end_utc"] = "Thu, 01 Jan 2026 00:00:00 GMT");
        yield return Case("row count mismatch", m => Source(m)["row_count"] = 199);
        yield return Case("evidence too short", m => Evidence(m).RemoveAt(199));
        yield return Case("evidence date mismatch", m => Evidence(m)[7]["date"] = "2001-01-01");
        yield return Case("period start after end", m => Evidence(m)[7]["period_start_utc"] = Evidence(m)[7]["period_end_utc"]);
        yield return Case("available before end", m => Evidence(m)[7]["available_at_utc"] = Evidence(m)[7]["period_start_utc"]);
        yield return Case("available after as_of", m => Evidence(m)[199]["available_at_utc"] = "2026-10-01T00:00:01Z");
        yield return Case("evidence time without offset", m => Evidence(m)[7]["period_end_utc"] = "2026-01-01T00:00:00");
        yield return Case("tombstone repeats a bar", m => Tombstones(m).Add(new() { ["anchor_utc"] = Evidence(m)[7]["period_start_utc"], ["available_at_utc"] = "2026-09-30T00:00:00Z" }));
        yield return Case("tombstone after as_of", m => Tombstones(m).Add(new() { ["anchor_utc"] = "2020-01-01T00:00:00Z", ["available_at_utc"] = "2026-10-01T00:00:01Z" }));
        yield return Case("tombstone reason not text", m => Tombstones(m).Add(new() { ["anchor_utc"] = "2020-01-01T00:00:00Z", ["available_at_utc"] = "2020-01-02T00:00:00Z", ["reason"] = 5 }));
        yield return Case("missing tombstones", m => m.Remove("tombstones"));
        yield return Case("missing evidence", m => m.Remove("bar_evidence"));
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task CorruptSidecar_IsRejectedBeforeAnySnapshotExists(string name, Action<Dictionary<string, object?>> mutate)
    {
        Write(mutate);
        await Assert.ThrowsAsync<InvalidDataException>(() => _source.Service.LoadAttestedObservationAsync(AttestedSourceFixture.Instrument, TimeFrame.D1));
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.x")]
    [InlineData("COM1")]
    [InlineData("lpt9.parquet")]
    [InlineData("e\u0301")]                                                                     // NFD, not NFC
    [InlineData("COM\u00b9")]                                                                   // superscript digit device names
    [InlineData("lpt\u00b3.parquet")]
    [InlineData("a\\b")]                                                                      // a Windows separator, rejected on every OS
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a\u0001b")]                                                                    // a control character
    [InlineData("inst.")]                                                                       // Windows drops a trailing period or space
    [InlineData("inst ")]
    public async Task InstrumentIdThatCannotBeAPlainFileName_IsRejectedBeforeAnyFileAccess(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _source.Service.LoadAttestedObservationAsync(id, TimeFrame.D1));
        Assert.False(AttestedFileNames.IsPlain(id));
    }

    [Fact]
    public void PlainFileNames_AreAccepted_AndAbsentOrRelativeNamesAreNot()
    {
        Assert.True(AttestedFileNames.IsPlain("INST") && AttestedFileNames.IsPlain("\u00e9") && AttestedFileNames.IsPlain("COM10")
            && AttestedFileNames.IsPlain("CONSOLE") && AttestedFileNames.IsPlain("COM0") && AttestedFileNames.IsPlain(".hidden") && AttestedFileNames.IsPlain("a-b_c.d"));
        Assert.False(AttestedFileNames.IsPlain(null) || AttestedFileNames.IsPlain(" ") || AttestedFileNames.IsPlain(".") || AttestedFileNames.IsPlain(".."));
    }

    private string ManifestJson() => System.Text.Json.JsonSerializer.Serialize(AttestedSourceFixture.Manifest(_bars, new string('a', 64), _kit.Clock.Now));

    [Theory]
    [InlineData("2026-10-01T00:00:00Z", true)]
    [InlineData("2026-10-01T00:00:00+00:00", true)]
    [InlineData("2026-10-01T00:00:00.123Z", true)]
    [InlineData("2026-10-01T00:00:00.1234567+00:00", true)]
    [InlineData("2026-10-01T00:00:00-00:00", false)]
    [InlineData("2026-10-01T00:00:00+0000", false)]
    [InlineData("2026-10-01T00:00:00.Z", false)]
    public void InstantSpelling_IsExactIso8601WithAZeroOffset(string text, bool accepted)
    {
        var original = AttestedSourceFixture.Manifest(_bars, new string('a', 64), _kit.Clock.Now);
        original["as_of_utc"] = text;
        var json = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(original));
        if (accepted) Assert.Equal(DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime, AttestationManifestReader.Parse(json).AsOfUtc.UtcDateTime);
        else Assert.Throws<InvalidDataException>(() => AttestationManifestReader.Parse(json));
    }

    [Theory]
    [InlineData("\"provider_id\":\"prov\"")]      // the root object
    [InlineData("\"row_count\":200")]             // the source object
    [InlineData("\"finality\":\"final\"")]        // a bar of the evidence array
    public void ADuplicateJsonKey_InAnyObject_IsRejected_EvenWithTheSameValue(string member)
    {
        var json = ManifestJson();
        Assert.Contains(member, json);
        var duplicated = json.Replace(member, member + "," + member, StringComparison.Ordinal);    // every occurrence repeats its key
        var bytes = System.Text.Encoding.UTF8.GetBytes(duplicated);
        var ex = Assert.Throws<InvalidDataException>(() => AttestationManifestReader.Parse(bytes));
        Assert.Contains("repeats", ex.Message);
        AttestationManifestReader.Parse(System.Text.Encoding.UTF8.GetBytes(json));                   // the original is fine
        Assert.Throws<InvalidDataException>(() => AttestationManifestReader.Parse([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(json)])); // a BOM was and stays rejected
    }

    [Fact]
    public async Task StaleWorkingCopies_AreRemovedOnceAtTheFirstLoad_RecentAndForeignFilesStay()
    {
        Write();
        Directory.CreateDirectory(_source.CopyRoot);
        var stale = Path.Combine(_source.CopyRoot, "stale.parquet"); var recent = Path.Combine(_source.CopyRoot, "recent.parquet");
        var foreign = Path.Combine(_source.CopyRoot, "notes.txt");
        foreach (var file in new[] { stale, recent, foreign }) File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(stale, _kit.Clock.Now.UtcDateTime.AddMinutes(-61));
        File.SetLastWriteTimeUtc(foreign, _kit.Clock.Now.UtcDateTime.AddDays(-30));
        File.SetLastWriteTimeUtc(recent, _kit.Clock.Now.UtcDateTime.AddMinutes(-59));
        Assert.NotNull(await _source.Service.LoadAttestedObservationAsync(AttestedSourceFixture.Instrument, TimeFrame.D1));
        Assert.False(File.Exists(stale)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(foreign));
        Assert.Empty(Directory.GetFiles(_source.CopyRoot, "*.parquet").Where(f => f != recent));   // this load's own copy is gone too

        File.WriteAllText(stale, "x"); File.SetLastWriteTimeUtc(stale, _kit.Clock.Now.UtcDateTime.AddDays(-1));
        Assert.NotNull(await _source.Service.LoadAttestedObservationAsync(AttestedSourceFixture.Instrument, TimeFrame.D1));
        Assert.True(File.Exists(stale));                                                              // swept once per service instance
    }

    [Fact]
    public async Task MalformedOrOversizedManifestAndTamperedParquet_AreIntegrityFaults()
    {
        Write();
        File.WriteAllText(Path.Combine(_source.Folder(TimeFrame.D1), "INST" + AttestationManifestReader.FileSuffix), "{ not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.D1));

        Write();
        using (var small = new AttestedSourceFixture(_kit.Clock, maxManifestBytes: 100))
        {
            small.WriteParquet(_bars); small.WriteManifest(AttestedSourceFixture.Manifest(_bars, "0".PadLeft(64, '0'), _kit.Clock.Now));
            await Assert.ThrowsAsync<InvalidDataException>(() => small.Service.LoadAttestedObservationAsync("INST", TimeFrame.D1));
        }

        Write();
        _source.WriteParquet(_bars.SetItem(10, _bars[10] with { Close = 123m, High = 123m }));  // the file changed after the manifest was written
        await Assert.ThrowsAsync<InvalidDataException>(() => _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.D1));
    }

    [Fact]
    public async Task WeeklyFolder_NeedsAWeeklyManifest_AndTheWorkingCopyIsRemoved()
    {
        var sha = _source.WriteParquet(_bars, timeframe: TimeFrame.W1);
        _source.WriteManifest(AttestedSourceFixture.Manifest(_bars, sha, _kit.Clock.Now, timeframe: "daily"), timeframe: TimeFrame.W1);
        await Assert.ThrowsAsync<InvalidDataException>(() => _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.W1)); // daily evidence never attests weekly bars

        _source.WriteManifest(AttestedSourceFixture.Manifest(_bars, sha, _kit.Clock.Now, timeframe: "weekly"), timeframe: TimeFrame.W1);
        var snapshot = await _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.W1);
        Assert.Equal(TimeframeType.Weekly, snapshot!.Timeframe);
        Assert.True(Directory.Exists(_source.CopyRoot));                                           // the copy was made inside the injected root ...
        Assert.Empty(Directory.GetFiles(_source.CopyRoot));                                        // ... and removed afterwards
    }

    [Fact]
    public async Task ProducerShapedFile_FlowsThroughTheStoreEndToEnd_MaturesThenRevokes()
    {
        await _kit.EnsureRegistryAsync();
        var enrollment = new AttestedSourceEnrollment { Symbol = "SYM", Timeframe = TimeframeType.Daily, ProviderId = "prov", InstrumentId = "INST" };
        var log = await _kit.CreateServiceAsync(attestedSources: [enrollment]);
        var id = await log.RecordAsync(_kit.Result!, "SYM", TimeframeType.Daily, _bars.Take(195).ToArray());

        Write();
        await log.RefreshAttestedAsync((await _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.D1))!);
        Assert.Equal(PredictionOutcomeState.Matured, (await log.ReadAuditAsync(id!))!.Outcomes[^1].State);

        var sha = _source.WriteParquet(_bars);
        var revoked = AttestedSourceFixture.Manifest(_bars, sha, _kit.Clock.Now, sequence: 2);
        Evidence(revoked)[197]["finality"] = "revoked";
        _source.WriteManifest(revoked);
        await log.RefreshAttestedAsync((await _source.Service.LoadAttestedObservationAsync("INST", TimeFrame.D1))!);
        var latest = (await log.ReadAuditAsync(id!))!.Outcomes[^1];
        Assert.Equal((PredictionOutcomeState.Pending, PendingReasons.ProviderFinalityRevoked), (latest.State, latest.PendingReason));
    }
}
