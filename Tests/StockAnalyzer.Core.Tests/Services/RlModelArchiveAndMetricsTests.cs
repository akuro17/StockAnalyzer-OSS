#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlModelArchiveAndMetricsTests : IDisposable
{
    private readonly string _testRoot;

    public RlModelArchiveAndMetricsTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "RlArchiveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public async Task ArchiveService_ExportAndImportCheckpoint_PreservesFilesAndHashes()
    {
        var service = new RlModelArchiveService();
        string sourceDir = Path.Combine(_testRoot, "checkpoint_step_100");
        Directory.CreateDirectory(sourceDir);

        string modelFile = Path.Combine(sourceDir, "model.onnx");
        string configFile = Path.Combine(sourceDir, "config.json");
        await File.WriteAllTextAsync(modelFile, "mock onnx model payload");
        await File.WriteAllTextAsync(configFile, "{\"epochs\": 10, \"lr\": 0.001}");

        string zipPath = Path.Combine(_testRoot, "exported_checkpoint.zip");
        string exportedPath = await service.ExportCheckpointArchiveAsync(sourceDir, zipPath);

        Assert.True(File.Exists(exportedPath));
        Assert.Equal(zipPath, exportedPath);

        // Verify zip contains manifest
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var manifestEntry = archive.GetEntry(RlModelArchiveService.ManifestEntryName);
            Assert.NotNull(manifestEntry);
            using var manifestStream = manifestEntry.Open();
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<RlArchiveManifest>(manifestStream);
            Assert.NotNull(manifest);
            Assert.Equal("checkpoint_step_100", manifest.Metadata["SourceDirectory"]);
            Assert.NotNull(archive.GetEntry("model.onnx"));
            Assert.NotNull(archive.GetEntry("config.json"));
        }

        // Test Import
        string importDir = Path.Combine(_testRoot, "imported");
        var result = await service.ImportArchiveAsync(zipPath, importDir);

        Assert.True(result.Success);
        Assert.Equal("Checkpoint", result.ArchiveKind);
        Assert.Equal("checkpoint_step_100", result.Id);
        Assert.Equal(2, result.FileCount);

        string importedModel = Path.Combine(result.TargetDirectory, "model.onnx");
        string importedConfig = Path.Combine(result.TargetDirectory, "config.json");
        Assert.True(File.Exists(importedModel));
        Assert.True(File.Exists(importedConfig));
        Assert.Equal("mock onnx model payload", await File.ReadAllTextAsync(importedModel));
    }

    [Fact]
    public async Task ArchiveService_ZipSlipAttempt_FailsSafelyWithoutEscapingTargetDirectory()
    {
        var service = new RlModelArchiveService();
        string maliciousZip = Path.Combine(_testRoot, "malicious.zip");

        // Manually create zip with relative traversal entry
        using (var zipStream = new FileStream(maliciousZip, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escaped_file.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("malicious payload");
        }

        string importDir = Path.Combine(_testRoot, "safe_target");
        var result = await service.ImportArchiveAsync(maliciousZip, importDir);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("ZipSlip", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_testRoot, "escaped_file.txt")));
    }

    [Fact]
    public async Task ArchiveService_ZipSlipAdjacentDirectoryAttempt_FailsSafely()
    {
        var service = new RlModelArchiveService();
        string maliciousZip = Path.Combine(_testRoot, "adjacent_malicious.zip");

        // Manually create zip with adjacent directory prefix attempt
        using (var zipStream = new FileStream(maliciousZip, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../safe_target_adjacent/escaped.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("malicious adjacent payload");
        }

        string importDir = Path.Combine(_testRoot, "safe_target");
        var result = await service.ImportArchiveAsync(maliciousZip, importDir);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("ZipSlip", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_testRoot, "safe_target_adjacent", "escaped.txt")));
    }

    [Theory]
    [InlineData("../traversal_id")]
    [InlineData("..\\traversal_id")]
    [InlineData("sub/folder")]
    [InlineData("sub\\folder")]
    [InlineData("id\0withnull")]
    public async Task ArchiveService_ManifestIdWithTraversalOrInvalidChars_FailsSafely(string maliciousId)
    {
        var service = new RlModelArchiveService();
        string maliciousZip = Path.Combine(_testRoot, "malicious_manifest_id_" + Guid.NewGuid().ToString("N") + ".zip");

        using (var zipStream = new FileStream(maliciousZip, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry(RlModelArchiveService.ManifestEntryName);
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                var manifest = new RlArchiveManifest(
                    SchemaVersion: 1,
                    ArchiveKind: "Checkpoint",
                    Id: maliciousId,
                    CreatedUtc: DateTimeOffset.UtcNow,
                    Metadata: new Dictionary<string, string>(),
                    FileHashes: new Dictionary<string, string>());
                writer.Write(System.Text.Json.JsonSerializer.Serialize(manifest));
            }

            var payloadEntry = archive.CreateEntry("model.onnx");
            using (var payloadWriter = new StreamWriter(payloadEntry.Open()))
            {
                payloadWriter.Write("payload");
            }
        }

        string importDir = Path.Combine(_testRoot, "safe_target_import_" + Guid.NewGuid().ToString("N"));
        var result = await service.ImportArchiveAsync(maliciousZip, importDir);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(Directory.Exists(Path.Combine(_testRoot, "traversal_id")));
        Assert.False(Directory.Exists(Path.Combine(importDir, "sub")));
    }

    [Fact]
    public void ArchiveKindConstants_PinTheValuesStoredInArchiveManifests()
    {
        // The kinds are written into exported archives, so their text is a file format and must not change with a rename.
        Assert.Equal("Checkpoint", RlModelArchiveService.CheckpointKind);
        Assert.Equal("Generation", RlModelArchiveService.GenerationKind);
        Assert.Equal("Generic", RlModelArchiveService.GenericKind);
    }

    private const string VerifiedArchiveId = "generation_verified";

    /// <summary>Builds a zip whose manifest lists <paramref name="hashes"/>; payload entries are written as given.</summary>
    private string BuildZip(
        Dictionary<string, byte[]> payload,
        Dictionary<string, string>? hashes,
        bool includeManifest = true)
    {
        string zipPath = Path.Combine(_testRoot, "built_" + Guid.NewGuid().ToString("N") + ".zip");
        using var zipStream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        foreach (var (name, bytes) in payload)
        {
            using var entryStream = archive.CreateEntry(name).Open();
            entryStream.Write(bytes, 0, bytes.Length);
        }

        if (includeManifest)
        {
            var manifest = new RlArchiveManifest(
                RlModelArchiveService.CurrentSchemaVersion,
                "Generation",
                VerifiedArchiveId,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>(),
                hashes!);
            using var manifestStream = archive.CreateEntry(RlModelArchiveService.ManifestEntryName).Open();
            System.Text.Json.JsonSerializer.Serialize(manifestStream, manifest);
        }

        return zipPath;
    }

    private static string HashOf(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static Dictionary<string, byte[]> SamplePayload() => new(StringComparer.Ordinal)
    {
        ["model.onnx"] = Encoding.UTF8.GetBytes("onnx payload"),
        ["sub/config.json"] = Encoding.UTF8.GetBytes("{\"lr\":0.001}"),
    };

    private static Dictionary<string, string> HashesOf(Dictionary<string, byte[]> payload)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, bytes) in payload) hashes[name] = HashOf(bytes);
        return hashes;
    }

    private static string[] FilesUnder(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories) : Array.Empty<string>();

    [Fact]
    public async Task ArchiveService_ImportWithMatchingHashes_ExtractsFilesAndLeavesNoStaging()
    {
        var payload = SamplePayload();
        string zipPath = BuildZip(payload, HashesOf(payload));
        string importDir = Path.Combine(_testRoot, "verified_ok");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FileCount);
        Assert.Equal("onnx payload", await File.ReadAllTextAsync(Path.Combine(result.TargetDirectory, "model.onnx")));
        Assert.True(File.Exists(Path.Combine(result.TargetDirectory, "sub", "config.json")));
        Assert.Empty(Directory.GetDirectories(result.TargetDirectory, ".import-*"));
    }

    [Fact]
    public async Task ArchiveService_ImportWithUpperCaseHashes_IsAccepted()
    {
        var payload = SamplePayload();
        var hashes = HashesOf(payload);
        foreach (string key in new List<string>(hashes.Keys)) hashes[key] = hashes[key].ToUpperInvariant();
        string zipPath = BuildZip(payload, hashes);

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, Path.Combine(_testRoot, "verified_upper"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public async Task ArchiveService_ImportWithTamperedByte_IsRejectedAndLeavesNoFiles()
    {
        var payload = SamplePayload();
        var hashes = HashesOf(payload);
        payload["model.onnx"][0] ^= 0x01;
        string zipPath = BuildZip(payload, hashes);
        string importDir = Path.Combine(_testRoot, "verified_tampered");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive entry 'model.onnx' failed SHA-256 verification.", result.Error);
        Assert.Empty(FilesUnder(importDir));
        Assert.False(Directory.Exists(Path.Combine(importDir, VerifiedArchiveId)));
    }

    [Fact]
    public async Task ArchiveService_ImportWithTamperedSecondEntry_DoesNotKeepTheValidFirstEntry()
    {
        var payload = SamplePayload();
        var hashes = HashesOf(payload);
        payload["sub/config.json"][0] ^= 0x01;
        string zipPath = BuildZip(payload, hashes);
        string importDir = Path.Combine(_testRoot, "verified_tampered_second");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal("Archive entry 'sub/config.json' failed SHA-256 verification.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    [Fact]
    public async Task ArchiveService_ImportWithEntryMissingFromManifest_IsRejected()
    {
        var payload = SamplePayload();
        var hashes = HashesOf(payload);
        hashes.Remove("sub/config.json");
        string zipPath = BuildZip(payload, hashes);
        string importDir = Path.Combine(_testRoot, "verified_unlisted");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive entry 'sub/config.json' is not listed in the manifest.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    [Fact]
    public async Task ArchiveService_ImportWithManifestEntryMissingFromArchive_IsRejected()
    {
        var payload = SamplePayload();
        var hashes = HashesOf(payload);
        hashes["missing.bin"] = HashOf(new byte[] { 1, 2, 3 });
        string zipPath = BuildZip(payload, hashes);
        string importDir = Path.Combine(_testRoot, "verified_missing");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive manifest lists 'missing.bin' but the archive does not contain it.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    [Fact]
    public async Task ArchiveService_ImportWithNullFileHashes_IsRejected()
    {
        string zipPath = BuildZip(SamplePayload(), hashes: null);
        string importDir = Path.Combine(_testRoot, "verified_null_hashes");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive manifest has no file hashes.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    /// <summary>A zip with the given entries in order (names may repeat) and a manifest that lists every distinct name.</summary>
    private string BuildZipWithEntries(params (string Name, string Text)[] entries)
    {
        string zipPath = Path.Combine(_testRoot, "entries_" + Guid.NewGuid().ToString("N") + ".zip");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in entries)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                hashes[name] = HashOf(bytes);
                using var entryStream = archive.CreateEntry(name).Open();
                entryStream.Write(bytes, 0, bytes.Length);
            }

            var manifest = new RlArchiveManifest(RlModelArchiveService.CurrentSchemaVersion, RlModelArchiveService.GenerationKind, VerifiedArchiveId,
                DateTimeOffset.UtcNow, new Dictionary<string, string>(), hashes);
            using var manifestStream = archive.CreateEntry(RlModelArchiveService.ManifestEntryName).Open();
            System.Text.Json.JsonSerializer.Serialize(manifestStream, manifest);
        }

        return zipPath;
    }

    [Fact]
    public async Task ArchiveService_ImportWithTheSameEntryNameTwice_IsRejectedAndLeavesNoFiles()
    {
        string zipPath = BuildZipWithEntries(("dup.bin", "first"), ("dup.bin", "second"));
        string importDir = Path.Combine(_testRoot, "verified_duplicate");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive entry 'dup.bin' collides with another entry of the archive.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    [Fact]
    public async Task ArchiveService_ImportWithNamesThatDifferOnlyByCaseOnACaseInsensitiveFileSystem_IsRejectedAndLeavesNoFiles()
    {
        if (!OperatingSystem.IsWindows()) return;   // on a case-sensitive file system these are two files and a legitimate archive
        string zipPath = BuildZipWithEntries(("a.bin", "lower"), ("A.bin", "upper"));
        string importDir = Path.Combine(_testRoot, "verified_case");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive entry 'A.bin' collides with another entry of the archive.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    [Fact]
    public async Task ArchiveService_ImportThatFailsWhilePublishing_RestoresTheFilesAlreadyReplacedAndLeavesNothingNew()
    {
        string importDir = Path.Combine(_testRoot, "verified_rollback");
        string targetDir = Path.Combine(importDir, VerifiedArchiveId);
        Directory.CreateDirectory(targetDir);
        string existing = Path.Combine(targetDir, "a.bin");
        File.WriteAllText(existing, "old content");
        Directory.CreateDirectory(Path.Combine(targetDir, "b.bin"));   // a folder where the second file must go: publishing it fails after a.bin was replaced
        string zipPath = BuildZipWithEntries(("a.bin", "new content"), ("b.bin", "second"));

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("old content", File.ReadAllText(existing));
        Assert.Equal(new[] { existing }, FilesUnder(targetDir));
        Assert.Empty(Directory.GetDirectories(targetDir, ".import-*"));
    }

    [Fact]
    public async Task ArchiveService_ImportOverAnExistingFolder_ReplacesTheFilesAndKeepsNoBackup()
    {
        string importDir = Path.Combine(_testRoot, "verified_replace");
        string targetDir = Path.Combine(importDir, VerifiedArchiveId);
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "a.bin"), "old content");
        string zipPath = BuildZipWithEntries(("a.bin", "new content"), ("b.bin", "second"));

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FileCount);
        Assert.Equal("new content", File.ReadAllText(Path.Combine(targetDir, "a.bin")));
        Assert.Equal(new[] { Path.Combine(targetDir, "a.bin"), Path.Combine(targetDir, "b.bin") }, FilesUnder(targetDir).OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ArchiveService_EveryRefusalCarriesAReasonAndTheArgumentItNames()
    {
        var service = new RlModelArchiveService(new RlArchiveLimits(32, 3));
        async Task<RlArchiveImportResult> Import(string zip, string name) => await service.ImportArchiveAsync(zip, Path.Combine(_testRoot, "reason_" + name));

        var payload = SamplePayload();
        var tampered = SamplePayload(); tampered["model.onnx"][0] ^= 0x01;
        var unlisted = HashesOf(payload); unlisted.Remove("sub/config.json");
        var missing = HashesOf(payload); missing["missing.bin"] = HashOf(new byte[] { 1 });
        var many = new Dictionary<string, byte[]> { ["a"] = new byte[] { 1 }, ["b"] = new byte[] { 1 }, ["c"] = new byte[] { 1 }, ["d"] = new byte[] { 1 } };
        var large = new Dictionary<string, byte[]> { ["large.bin"] = new byte[64] };

        var cases = new (RlArchiveImportResult Result, RlArchiveImportFailure Reason, string? Argument)[]
        {
            (await Import(Path.Combine(_testRoot, "none.zip"), "notfound"), RlArchiveImportFailure.NotFound, null),
            (await Import(BuildZip(payload, HashesOf(tampered)), "hash"), RlArchiveImportFailure.HashMismatch, "model.onnx"),
            (await Import(BuildZip(payload, unlisted), "unlisted"), RlArchiveImportFailure.EntryNotListed, "sub/config.json"),
            (await Import(BuildZip(payload, missing), "missing"), RlArchiveImportFailure.ManifestEntryMissing, "missing.bin"),
            (await Import(BuildZip(payload, null), "nohashes"), RlArchiveImportFailure.MissingFileHashes, null),
            (await Import(BuildZipWithEntries(("dup.bin", "1"), ("dup.bin", "2")), "collision"), RlArchiveImportFailure.EntryCollision, "dup.bin"),
            (await Import(BuildZip(many, HashesOf(many)), "many"), RlArchiveImportFailure.TooManyEntries, "3"),
            (await Import(BuildZip(large, HashesOf(large)), "large"), RlArchiveImportFailure.EntryTooLarge, "large.bin"),
        };

        foreach (var (result, reason, argument) in cases)
        {
            Assert.False(result.Success);
            Assert.Equal(reason, result.Reason);
            Assert.Equal(argument, result.ReasonArgument);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
    }

    [Fact]
    public async Task ArchiveService_ZipSlipAndInvalidManifestIdCarryTheirReasons()
    {
        string slip = Path.Combine(_testRoot, "reason_slip.zip");
        using (var stream = new FileStream(slip, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()))
            writer.Write("x");
        var payload = SamplePayload();
        string badId = BuildZip(payload, HashesOf(payload));
        using (var archive = ZipFile.Open(badId, ZipArchiveMode.Update))
        {
            archive.GetEntry(RlModelArchiveService.ManifestEntryName)!.Delete();
            var manifest = new RlArchiveManifest(1, "Generation", "..", DateTimeOffset.UtcNow, new Dictionary<string, string>(), HashesOf(payload));
            using var manifestStream = archive.CreateEntry(RlModelArchiveService.ManifestEntryName).Open();
            System.Text.Json.JsonSerializer.Serialize(manifestStream, manifest);
        }

        var slipResult = await new RlModelArchiveService().ImportArchiveAsync(slip, Path.Combine(_testRoot, "reason_slip_target"));
        var idResult = await new RlModelArchiveService().ImportArchiveAsync(badId, Path.Combine(_testRoot, "reason_id_target"));

        Assert.Equal((RlArchiveImportFailure.PathEscape, "../escaped.txt"), (slipResult.Reason, slipResult.ReasonArgument));
        Assert.Equal((RlArchiveImportFailure.InvalidManifestId, ".."), (idResult.Reason, idResult.ReasonArgument));
        Assert.Contains("ZipSlip", slipResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchiveLimits_DefaultAreDerivedFromTheCapturedFileBoundAndTheLargestGenerationAndRejectNonPositiveValues()
    {
        Assert.Equal(RlContract.BufferByteLimit, RlArchiveLimits.Default.MaxEntryBytes);
        Assert.Equal(StockAnalyzer.Core.Services.ModelGenerationRegistry.MaxRlGenerationFileCount + 1, RlArchiveLimits.Default.MaxEntries);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RlModelArchiveService(new RlArchiveLimits(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RlModelArchiveService(new RlArchiveLimits(1, 0)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArchiveService_ImportWithMoreEntriesThanTheLimit_IsRejectedBeforeAnythingIsExtracted(bool withManifest)
    {
        var payload = new Dictionary<string, byte[]> { ["a.bin"] = new byte[] { 1 }, ["b.bin"] = new byte[] { 2 }, ["c.bin"] = new byte[] { 3 } };
        string zipPath = withManifest ? BuildZip(payload, HashesOf(payload)) : BuildZip(payload, null, includeManifest: false);
        string importDir = Path.Combine(_testRoot, "limit_entries_" + withManifest);

        var result = await new RlModelArchiveService(new RlArchiveLimits(1_024, 2)).ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive holds more than 2 entries.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArchiveService_ImportWithAnEntryLargerThanTheLimit_IsRejectedAndLeavesNoFiles(bool withManifest)
    {
        var payload = new Dictionary<string, byte[]> { ["small.bin"] = new byte[] { 1 }, ["large.bin"] = new byte[64] };
        string zipPath = withManifest ? BuildZip(payload, HashesOf(payload)) : BuildZip(payload, null, includeManifest: false);
        string importDir = Path.Combine(_testRoot, "limit_bytes_" + withManifest);

        var result = await new RlModelArchiveService(new RlArchiveLimits(16, 10)).ImportArchiveAsync(zipPath, importDir);

        Assert.False(result.Success);
        Assert.Equal(0, result.FileCount);
        Assert.Equal("Archive entry 'large.bin' exceeds the size limit of 16 bytes.", result.Error);
        Assert.Empty(FilesUnder(importDir));
    }

    [Fact]
    public async Task ArchiveService_ImportAtExactlyTheLimits_Succeeds()
    {
        var payload = new Dictionary<string, byte[]> { ["a.bin"] = new byte[16], ["b.bin"] = new byte[16] };
        string zipPath = BuildZip(payload, HashesOf(payload));

        var result = await new RlModelArchiveService(new RlArchiveLimits(16, 2)).ImportArchiveAsync(zipPath, Path.Combine(_testRoot, "limit_exact"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public async Task ArchiveService_ImportEmptyArchiveWithEmptyHashes_SucceedsWithZeroFiles()
    {
        string zipPath = BuildZip(new Dictionary<string, byte[]>(), new Dictionary<string, string>());

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, Path.Combine(_testRoot, "verified_empty"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.FileCount);
    }

    [Fact]
    public async Task ArchiveService_ImportEmptyArchiveWithListedHashes_IsRejected()
    {
        var hashes = new Dictionary<string, string> { ["model.onnx"] = HashOf(new byte[] { 1 }) };
        string zipPath = BuildZip(new Dictionary<string, byte[]>(), hashes);

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, Path.Combine(_testRoot, "verified_empty_listed"));

        Assert.False(result.Success);
        Assert.Equal("Archive manifest lists 'model.onnx' but the archive does not contain it.", result.Error);
    }

    [Fact]
    public async Task ArchiveService_ImportWithoutManifest_StillImportsAsGeneric()
    {
        var payload = SamplePayload();
        string zipPath = BuildZip(payload, hashes: null, includeManifest: false);
        string importDir = Path.Combine(_testRoot, "no_manifest");

        var result = await new RlModelArchiveService().ImportArchiveAsync(zipPath, importDir);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Generic", result.ArchiveKind);
        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(importDir, "model.onnx")));
    }
}
