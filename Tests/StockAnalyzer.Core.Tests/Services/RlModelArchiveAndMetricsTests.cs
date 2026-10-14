#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
}
