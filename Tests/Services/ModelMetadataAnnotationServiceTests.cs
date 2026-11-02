using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ModelMetadataAnnotationServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testFilePath;

    public ModelMetadataAnnotationServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "StockAnalyzerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _testFilePath = Path.Combine(_testDir, "model_annotations.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task Lifecycle_SetResolveAndClear_WorksCorrectly()
    {
        var service = new ModelMetadataAnnotationService(_testFilePath);
        await service.InitializeAsync();

        var key = new ModelMetadataKey(ModelArtifactKind.Prediction, "model-123");

        // Initial fallback
        var initial = service.ResolveDisplayName(key, "default.onnx");
        Assert.Equal("default.onnx", initial);
        Assert.Null(service.GetAnnotation(key));

        // Set custom name
        var updated = await service.SetCustomDisplayNameAsync(key, "Nikkei Predictor v1", reason: "Manual rename");
        Assert.Equal("Nikkei Predictor v1", updated.CustomDisplayName);
        Assert.Single(updated.History);
        Assert.Equal("CustomDisplayName", updated.History[0].FieldName);
        Assert.Null(updated.History[0].OldValue);
        Assert.Equal("Nikkei Predictor v1", updated.History[0].NewValue);
        Assert.Equal("Manual rename", updated.History[0].Reason);

        // Resolve
        Assert.Equal("Nikkei Predictor v1", service.ResolveDisplayName(key, "default.onnx"));
        Assert.Equal("Nikkei Predictor v1", service.ResolveDisplayName("model-123", "default.onnx"));

        // Update custom name
        var updated2 = await service.SetCustomDisplayNameAsync(key, "Nikkei Predictor v2", reason: "Version bump");
        Assert.Equal("Nikkei Predictor v2", updated2.CustomDisplayName);
        Assert.Equal(2, updated2.History.Count);
        Assert.Equal("Nikkei Predictor v1", updated2.History[1].OldValue);
        Assert.Equal("Nikkei Predictor v2", updated2.History[1].NewValue);

        // Clear custom name
        var cleared = await service.ClearCustomDisplayNameAsync(key);
        Assert.Null(cleared.CustomDisplayName);
        Assert.Equal(3, cleared.History.Count);
        Assert.Equal("default.onnx", service.ResolveDisplayName(key, "default.onnx"));

        // Reload fresh service instance to verify persistence
        var service2 = new ModelMetadataAnnotationService(_testFilePath);
        await service2.InitializeAsync();
        var annotation2 = service2.GetAnnotation(key);
        Assert.NotNull(annotation2);
        Assert.Null(annotation2.CustomDisplayName);
        Assert.Equal(3, annotation2.History.Count);
    }

    [Fact]
    public async Task Validation_LengthBoundary_Enforced()
    {
        var service = new ModelMetadataAnnotationService(_testFilePath);
        await service.InitializeAsync();

        var key = new ModelMetadataKey(ModelArtifactKind.Prediction, "len-test");

        string exact128 = new('a', 128);
        var result = await service.SetCustomDisplayNameAsync(key, exact128);
        Assert.Equal(exact128, result.CustomDisplayName);

        string over128 = new('a', 129);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.SetCustomDisplayNameAsync(key, over128));
        Assert.Contains("128", ex.Message);
    }

    [Theory]
    [InlineData("Name\nWithNewline")]
    [InlineData("Name\rWithReturn")]
    [InlineData("Name\tWithTab")]
    [InlineData("Name\0WithNull")]
    [InlineData("Name\u007fWithDel")]
    public async Task Validation_ControlCharacters_Rejected(string invalidName)
    {
        var service = new ModelMetadataAnnotationService(_testFilePath);
        await service.InitializeAsync();

        var key = new ModelMetadataKey(ModelArtifactKind.Prediction, "ctrl-test");
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.SetCustomDisplayNameAsync(key, invalidName));
        Assert.Contains("control characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoOp_IdenticalDisplayName_DoesNotAppendHistoryOrIncrementRevision()
    {
        var service = new ModelMetadataAnnotationService(_testFilePath);
        await service.InitializeAsync();

        var key = new ModelMetadataKey(ModelArtifactKind.Prediction, "noop-test");

        // Initial set
        var first = await service.SetCustomDisplayNameAsync(key, "My Model");
        Assert.Single(first.History);

        var fileInfoBefore = new FileInfo(_testFilePath);
        var lastWriteBefore = fileInfoBefore.LastWriteTimeUtc;

        // No-op set with extra surrounding whitespace that trims to same
        var second = await service.SetCustomDisplayNameAsync(key, "  My Model  ");
        Assert.Same(first, second);
        Assert.Single(second.History);

        // No-op clear on unassigned model
        var unassignedKey = new ModelMetadataKey(ModelArtifactKind.Prediction, "unassigned-model");
        var cleared = await service.ClearCustomDisplayNameAsync(unassignedKey);
        Assert.Null(cleared.CustomDisplayName);
        Assert.Empty(cleared.History);
    }

    [Fact]
    public async Task Concurrency_100ParallelUpdates_SucceedWithoutLostUpdatesOrIoException()
    {
        var service = new ModelMetadataAnnotationService(_testFilePath);
        await service.InitializeAsync();

        int count = 100;
        var tasks = Enumerable.Range(0, count).Select(i =>
        {
            var key = new ModelMetadataKey(ModelArtifactKind.Prediction, $"concurrent-{i}");
            return service.SetCustomDisplayNameAsync(key, $"Model Display {i}");
        }).ToArray();

        await Task.WhenAll(tasks);

        // Verify all 100 entries persisted
        var service2 = new ModelMetadataAnnotationService(_testFilePath);
        await service2.InitializeAsync();

        for (int i = 0; i < count; i++)
        {
            var key = new ModelMetadataKey(ModelArtifactKind.Prediction, $"concurrent-{i}");
            Assert.Equal($"Model Display {i}", service2.ResolveDisplayName(key, "none"));
        }
    }

    [Fact]
    public async Task HistoryTrimming_RetainsUpTo100Entries()
    {
        var service = new ModelMetadataAnnotationService(_testFilePath);
        await service.InitializeAsync();

        var key = new ModelMetadataKey(ModelArtifactKind.Prediction, "trim-test");

        for (int i = 1; i <= 105; i++)
        {
            await service.SetCustomDisplayNameAsync(key, $"Version {i}");
        }

        var annotation = service.GetAnnotation(key);
        Assert.NotNull(annotation);
        Assert.Equal("Version 105", annotation.CustomDisplayName);
        Assert.Equal(100, annotation.History.Count);
        Assert.Equal("Version 105", annotation.History[^1].NewValue);
        Assert.Equal(105, annotation.History[^1].Sequence);
        Assert.Equal(6, annotation.History[0].Sequence);
    }

    [Fact]
    public async Task CorruptionRecovery_QuarantinesCorruptFile_AndRestoresBackup()
    {
        var service = new ModelMetadataAnnotationService(_testFilePath);
        await service.InitializeAsync();

        var key = new ModelMetadataKey(ModelArtifactKind.Prediction, "backup-test");
        await service.SetCustomDisplayNameAsync(key, "Valid Model Name");

        // Manually create a valid backup file
        var backupPath = _testFilePath + AtomicJsonFile.BackupSuffix;
        File.Copy(_testFilePath, backupPath, overwrite: true);

        // Corrupt primary file
        await File.WriteAllTextAsync(_testFilePath, "{ this is not valid json !!!");

        // Initialize new service instance
        var recoveringService = new ModelMetadataAnnotationService(_testFilePath);
        await recoveringService.InitializeAsync();

        // Check quarantine file was created
        var corruptedFiles = Directory.GetFiles(_testDir, "model_annotations.json.corrupted.*");
        Assert.Single(corruptedFiles);

        // Verified restored from backup
        var annotation = recoveringService.GetAnnotation(key);
        Assert.NotNull(annotation);
        Assert.Equal("Valid Model Name", annotation.CustomDisplayName);
    }
}
