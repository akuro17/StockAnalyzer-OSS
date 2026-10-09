#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Pins the behavior of <see cref="RlSourceSnapshot.Load"/> before its per-file validation was extracted for the panel snapshot:
/// every validation branch with its exception type and message, the order of the checks, the revision and the catalog.
/// The same tests must pass unchanged after the extraction.
/// </summary>
public sealed class RlSourceLoadCharacterizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_source_load_" + Guid.NewGuid().ToString("N"));
    private const int Short = 300;

    public RlSourceLoadCharacterizationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Source(int count = Short, string symbol = "FIXTURE") => RlSourceFixture.WriteSource(_root, count, symbol);

    private static void Rehash(string directory)
    {
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root =>
        {
            root["prepared_file_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlFiles.Bars));
            root["source_file_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlFiles.RawParquet));
            root["metadata_file_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlFiles.MetadataParquet));
        });
    }

    private static void AssertRejected(string directory, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(directory, RlSourceFixture.Limits));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("schema_version", 1)]
    [InlineData("provider", "other")]
    [InlineData("instrument_type", "ETF")]
    [InlineData("adjustment", "adjusted")]
    [InlineData("current_history_revision", true)]
    [InlineData("original_acquisition_status", "verified")]
    public void ProvenanceContractViolationsAreRejected(string field, object value)
    {
        string directory = Source();
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root => root[field] = System.Text.Json.JsonSerializer.SerializeToNode(value));
        AssertRejected(directory, "RL source provenance/adjustment/availability contract is unsupported.");
    }

    [Fact]
    public void ImputedDatesAreRejected()
    {
        string directory = Source();
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root => root["known_imputed_dates"] = new JsonArray("2000-01-05"));
        AssertRejected(directory, "RL source provenance/adjustment/availability contract is unsupported.");
    }

    [Theory]
    [InlineData("mapped_symbol", "A/B")]
    [InlineData("source_path_identity", "Daily/OTHER.parquet")]
    [InlineData("metadata_path_identity", "Metadata/OTHER.meta.parquet")]
    public void PathIdentityViolationsAreRejected(string field, string value)
    {
        string directory = Source();
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root => root[field] = value);
        AssertRejected(directory, "Source path identities must describe the approved local Daily and Metadata files.");
    }

    [Fact]
    public void CapturedExchangeDateMustMatchTheSnapshotStart()
    {
        string directory = Source();
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root => root["captured_exchange_date"] = "2000-02-01");
        AssertRejected(directory, "Captured exchange date differs from immutable snapshot-start metadata.");
    }

    [Fact]
    public void IngressParametersMustMatchTheContract()
    {
        string directory = Source();
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root => root["acquisition_parameters"]!["network"] = true);
        AssertRejected(directory, "Local ingress parameter network mismatches the RL contract.");
    }

    [Fact]
    public void ChangedBarsFileFailsTheHashCheck()
    {
        string directory = Source();
        File.AppendAllText(Path.Combine(directory, RlFiles.Bars), " ");
        AssertRejected(directory, "Source snapshot hash mismatch.");
    }

    [Fact]
    public void NonIncreasingBarsAreRejectedWithoutRepair()
    {
        string directory = Source();
        var bars = RlSourceFixture.Bars(RlSourceFixture.FirstDay, Short).ToArray();
        bars[1] = bars[0];
        RlSourceFixture.WriteJson(Path.Combine(directory, RlFiles.Bars), bars);
        Rehash(directory);
        AssertRejected(directory, "Invalid or unavailable source bar 1; no repair is permitted.");
    }

    [Fact]
    public void RowCountMustMatchTheBars()
    {
        string directory = Source();
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root => root["row_count"] = Short - 1);
        AssertRejected(directory, "Source arrays/count/resource bounds are invalid.");
    }

    [Fact]
    public void DeclaredFirstDateMustMatchTheBars()
    {
        string directory = Source();
        RlSourceFixture.Mutate(directory, RlFiles.SourceManifest, root => root["first_date"] = "2000-01-02");
        AssertRejected(directory, "Source declared first/last dates differ from immutable prepared bars.");
    }

    [Fact]
    public void BarsAtOrAfterTheCapturedDateAreRejected()
    {
        string directory = RlSourceFixture.WriteSource(_root, Short, capturedExchangeDate: RlSourceFixture.FirstDay.AddDays(Short - 1));
        AssertRejected(directory, "Invalid or unavailable source bar " + (Short - 1) + "; no repair is permitted.");
    }

    [Fact]
    public void StoredMetadataMustMatchTheDeclaredValues()
    {
        string directory = Source();
        RlSourceFixture.CreateParquet(Path.Combine(directory, RlFiles.MetadataParquet),
            "SELECT 'JPY' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        Rehash(directory);
        AssertRejected(directory, "Declared local metadata differs from the copied stored values.");
    }

    [Fact]
    public void CopiedParquetMustEqualThePreparedBars()
    {
        string directory = Source();
        RlSourceFixture.CreateParquet(Path.Combine(directory, RlFiles.RawParquet),
            $"SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date,101::FLOAT AS open,111::FLOAT AS high,91::FLOAT AS low,101::FLOAT AS close,1000::BIGINT AS volume FROM range({Short})");
        Rehash(directory);
        AssertRejected(directory, "Prepared decimal bars or precision differ from the copied local Parquet ingress.");
    }

    [Fact]
    public void MissingFilesAndArgumentsAreReportedByType()
    {
        string directory = Source();
        File.Delete(Path.Combine(directory, RlFiles.SourceManifest));
        Assert.Throws<FileNotFoundException>(() => RlSourceSnapshot.Load(directory, RlSourceFixture.Limits));
        Assert.ThrowsAny<IOException>(() => RlSourceSnapshot.Load(Path.Combine(_root, "absent"), RlSourceFixture.Limits));
        Assert.Throws<ArgumentNullException>(() => RlSourceSnapshot.Load(directory, null!));
    }

    [Fact]
    public void SpecificationChecksComeBeforeFileValidation()
    {
        string directory = Source();
        RlSourceFixture.WriteJson(Path.Combine(directory, RlFiles.Job), new { evaluation_specification_id = "unknown" });
        File.AppendAllText(Path.Combine(directory, RlFiles.Bars), " ");
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(directory, RlSourceFixture.Limits));
        Assert.Equal("Unsupported independent RL evaluation specification.", error.Message);
    }

    [Fact]
    public void RequestedAndSavedSpecificationsMustAgree()
    {
        string directory = Source();
        File.WriteAllText(Path.Combine(directory, RlFiles.Job), "{}");
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(directory, RlSourceFixture.Limits, false,
            CancellationToken.None, RlAllBlockEvaluationContract.SpecificationId));
        Assert.Equal("Requested and saved evaluation specifications differ.", error.Message);
    }

    [Fact]
    public void AllBlockAndShortHistoryAreMutuallyExclusive()
    {
        string directory = Source();
        RlSourceFixture.WriteJson(Path.Combine(directory, RlFiles.Job), new System.Collections.Generic.Dictionary<string, string>
        {
            [RlAllBlockEvaluationContract.MetadataKey] = RlAllBlockEvaluationContract.SpecificationId,
            [RlShortHistoryEvaluationContract.MetadataKey] = RlShortHistoryEvaluationContract.SpecificationId,
        });
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(directory, RlSourceFixture.Limits));
        Assert.Contains("mutually exclusive", error.Message);
    }

    [Fact]
    public void ValidSourceLoadsWithTheDocumentedRevisionAndCatalog()
    {
        string directory = Source(5_200);
        var snapshot = RlSourceSnapshot.Load(directory, RlSourceFixture.Limits);
        Assert.Equal(Path.GetFullPath(directory), snapshot.Directory);
        Assert.Equal(ContentHash.OfFile(Path.Combine(directory, RlFiles.SourceManifest)), snapshot.Revision);
        Assert.Equal(5_200, snapshot.Bars.Length);
        Assert.Equal("FIXTURE", snapshot.Manifest.Symbol);
        // Hand table: cut1 = 3*5200/5 = 3120, cut2 = 4*5200/5 = 4160; train blocks 0,192,..,2880 (16); validation 3184,..,3952 (5); oos 4224,..,4992 (5).
        Assert.Equal(3_120, snapshot.Catalog.TrainCut);
        Assert.Equal(4_160, snapshot.Catalog.ValidationCut);
        Assert.Equal(16, snapshot.Catalog.Train.Length);
        Assert.Equal(new[] { 3184, 3376, 3568, 3760, 3952 }, snapshot.Catalog.Validation.Select(b => b.StartIndex).ToArray());
        Assert.Equal(new[] { 4224, 4416, 4608, 4800, 4992 }, snapshot.Catalog.Oos.Select(b => b.StartIndex).ToArray());
        Assert.False(snapshot.ExpandedEvaluation);
        Assert.False(snapshot.ShortHistoryEvaluation);
    }

    [Fact]
    public void ShortSourceIsRejectedOnlyAfterTheFilesValidate()
    {
        string valid = Source();
        var insufficient = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(valid, RlSourceFixture.Limits));
        Assert.True(RlSourceContract.IsInsufficientData(insufficient));

        string tampered = Source();
        File.AppendAllText(Path.Combine(tampered, RlFiles.Bars), " ");
        var hash = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(tampered, RlSourceFixture.Limits));
        Assert.False(RlSourceContract.IsInsufficientData(hash));
        Assert.Contains("Source snapshot hash mismatch.", hash.Message);
    }

    [Theory]
    [InlineData(1_277, false)]
    [InlineData(1_278, true)]
    public void SmokeBoundaryIsTheDerivedSupportMinimum(int count, bool loads)
    {
        string directory = Source(count);
        if (loads) Assert.Empty(RlSourceSnapshot.Load(directory, RlSourceFixture.Limits, true).Catalog.Oos);
        else Assert.True(RlSourceContract.IsInsufficientData(
            Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(directory, RlSourceFixture.Limits, true))));
    }

    [Fact]
    public void LoadNeverWritesIntoTheSnapshotDirectory()
    {
        string directory = Source(5_200);
        string before = RlSourceFixture.TreeDigest(directory);
        _ = RlSourceSnapshot.Load(directory, RlSourceFixture.Limits);
        Assert.Equal(before, RlSourceFixture.TreeDigest(directory));
    }
}
