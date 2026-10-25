using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>
    /// <see cref="ModelDeploymentService"/> resolves its target through the real, non-mockable
    /// <see cref="PathDiscovery.ResolvePredictionModelPath"/> (the same resolver
    /// <c>PredictionService</c> uses), so these tests write under the real
    /// <c>&lt;DataRoot&gt;/Models/</c> directory using a unique <c>sa_test_</c>-prefixed filename
    /// per test and always delete what they created (see <see cref="Dispose"/>), matching the
    /// convention already used by <c>PathDiscoveryTests</c>.
    /// </summary>
    public class ModelDeploymentServiceTests : IDisposable
    {
        private readonly string _sourceDir;
        private readonly List<string> _deployedPaths = new();

        public ModelDeploymentServiceTests()
        {
            _sourceDir = Path.Combine(Path.GetTempPath(), "sa_deploy_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sourceDir);
        }

        public void Dispose()
        {
            foreach (var path in _deployedPaths)
            {
                TryDelete(path);
                TryDelete(path + ".tmp");
                TryDelete(path + ".metrics.json");
                TryDelete(path + ".metrics.json.tmp");
            }
            try { Directory.Delete(_sourceDir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }

        [Fact]
        public async Task DeployAsync_ValidOnnxOnly_CopiesToModelsDirWithSameFileName()
        {
            var fileName = "sa_test_deploy_" + Guid.NewGuid().ToString("N") + ".onnx";
            var sourcePath = WriteSourceFile(fileName, "fake-onnx-bytes");
            var service = new ModelDeploymentService();

            var finalPath = await service.DeployAsync(sourcePath);
            _deployedPaths.Add(finalPath);

            Assert.Equal(fileName, Path.GetFileName(finalPath));
            Assert.Equal("Models", Path.GetFileName(Path.GetDirectoryName(finalPath))!);
            Assert.True(File.Exists(finalPath));
            Assert.Equal("fake-onnx-bytes", await File.ReadAllTextAsync(finalPath));
            Assert.False(File.Exists(finalPath + ".tmp"), "temp file must not survive a successful deploy");
        }

        [Fact]
        public async Task DeployAsync_WithMetricsSidecar_DeploysBothFilesUnderMatchingNames()
        {
            var fileName = "sa_test_deploy_" + Guid.NewGuid().ToString("N") + ".onnx";
            var sourcePath = WriteSourceFile(fileName, "fake-onnx-bytes");
            var metricsSourcePath = WriteSourceFile(fileName + ".metrics.json", "{\"accuracy\":0.5}");
            var service = new ModelDeploymentService();

            var finalPath = await service.DeployAsync(sourcePath, metricsSourcePath);
            _deployedPaths.Add(finalPath);

            var finalMetricsPath = finalPath + ".metrics.json";
            Assert.True(File.Exists(finalMetricsPath));
            Assert.Equal("{\"accuracy\":0.5}", await File.ReadAllTextAsync(finalMetricsPath));
        }

        [Fact]
        public async Task DeployAsync_MissingMetricsSourcePath_DeploysOnnxOnlyWithoutError()
        {
            var fileName = "sa_test_deploy_" + Guid.NewGuid().ToString("N") + ".onnx";
            var sourcePath = WriteSourceFile(fileName, "fake-onnx-bytes");
            var service = new ModelDeploymentService();

            var finalPath = await service.DeployAsync(sourcePath, metricsSourcePath: null);
            _deployedPaths.Add(finalPath);

            Assert.True(File.Exists(finalPath));
            Assert.False(File.Exists(finalPath + ".metrics.json"));
        }

        [Fact]
        public async Task DeployAsync_Redeploy_OverwritesWithNewContentAndLeavesNoTempFile()
        {
            var fileName = "sa_test_deploy_" + Guid.NewGuid().ToString("N") + ".onnx";
            var service = new ModelDeploymentService();

            var firstSource = WriteSourceFile(fileName, "first-version");
            var finalPath = await service.DeployAsync(firstSource);
            _deployedPaths.Add(finalPath);
            File.Delete(firstSource);

            var secondSource = WriteSourceFile(fileName, "second-version");
            var redeployedPath = await service.DeployAsync(secondSource);

            Assert.Equal(finalPath, redeployedPath);
            Assert.Equal("second-version", await File.ReadAllTextAsync(finalPath));
            Assert.False(File.Exists(finalPath + ".tmp"));
        }

        [Fact]
        public async Task DeployAsync_SourceDoesNotExist_ThrowsFileNotFoundException()
        {
            var missingPath = Path.Combine(_sourceDir, "does_not_exist.onnx");
            var service = new ModelDeploymentService();

            await Assert.ThrowsAsync<FileNotFoundException>(() => service.DeployAsync(missingPath));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task DeployAsync_BlankOnnxSourcePath_ThrowsArgumentException(string blank)
        {
            var service = new ModelDeploymentService();

            await Assert.ThrowsAsync<ArgumentException>(() => service.DeployAsync(blank));
        }

        [Fact]
        public async Task PromoteTempRunToTrainingArtifactsAsync_ValidTempRun_CopiesAllSidecarsAndLeavesSourceIntact()
        {
            var targetDir = Path.Combine(Path.GetTempPath(), "sa_deploy_dest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(targetDir);
            try
            {
                var runId = "20261025-120000000-abcdef12";
                var onnxFile = WriteSourceFile("model.onnx", "onnx-binary-content");
                var metricsFile = WriteSourceFile("model.onnx.metrics.json", "{\"accuracy\":0.85}");
                var scalerFile = WriteSourceFile("model.onnx.scaler.json", "{\"schema_version\":1}");

                var result = new TrainingRunResult
                {
                    RunId = runId,
                    Success = true,
                    ExitCode = 0,
                    OnnxArtifactPath = onnxFile,
                    MetricsArtifactPath = metricsFile,
                };

                var service = new ModelDeploymentService();
                var promoted = await service.PromoteTempRunToTrainingArtifactsAsync(result, targetDir);

                // Promoted paths point to targetDir
                Assert.Equal(targetDir, Path.GetDirectoryName(promoted.OnnxArtifactPath));
                Assert.Equal("model.onnx", Path.GetFileName(promoted.OnnxArtifactPath));
                Assert.True(File.Exists(promoted.OnnxArtifactPath));
                Assert.Equal("onnx-binary-content", await File.ReadAllTextAsync(promoted.OnnxArtifactPath!));

                Assert.Equal(targetDir, Path.GetDirectoryName(promoted.MetricsArtifactPath));
                Assert.True(File.Exists(promoted.MetricsArtifactPath));
                Assert.Equal("{\"accuracy\":0.85}", await File.ReadAllTextAsync(promoted.MetricsArtifactPath!));

                // Sidecar scaler copied too
                var destScaler = Path.Combine(targetDir, "model.onnx.scaler.json");
                Assert.True(File.Exists(destScaler));
                Assert.Equal("{\"schema_version\":1}", await File.ReadAllTextAsync(destScaler));

                // Source directory files remain intact
                Assert.True(File.Exists(onnxFile));
                Assert.True(File.Exists(metricsFile));
                Assert.True(File.Exists(scalerFile));
            }
            finally
            {
                try { Directory.Delete(targetDir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public async Task PromoteTempRunToTrainingArtifactsAsync_AlreadyInTargetDir_ReturnsImmediately()
        {
            var onnxFile = WriteSourceFile("model.onnx", "onnx-content");
            var result = new TrainingRunResult
            {
                RunId = "run-same-dir",
                Success = true,
                OnnxArtifactPath = onnxFile,
            };

            var service = new ModelDeploymentService();
            var promoted = await service.PromoteTempRunToTrainingArtifactsAsync(result, _sourceDir);

            Assert.Same(result, promoted);
        }

        [Fact]
        public async Task PromoteTempRunToTrainingArtifactsAsync_UnsuccessfulRun_ThrowsInvalidOperationException()
        {
            var result = new TrainingRunResult
            {
                RunId = "run-failed",
                Success = false,
                OnnxArtifactPath = Path.Combine(_sourceDir, "model.onnx"),
            };

            var service = new ModelDeploymentService();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PromoteTempRunToTrainingArtifactsAsync(result, _sourceDir));
        }

        [Fact]
        public async Task PromoteTempRunToTrainingArtifactsAsync_DefaultDestination_SeparatesByRunIdAndFiltersUnrelatedFiles()
        {
            var onnxFile = WriteSourceFile("model.onnx", "onnx-run1");
            var metricsFile = WriteSourceFile("model.onnx.metrics.json", "{\"loss\":0.1}");
            var unrelatedFile = WriteSourceFile("unrelated_notes.txt", "ignore-me");
            var unrelatedJson = WriteSourceFile("random_data.json", "{\"ignored\":true}");

            var runId1 = "run-default-1";
            var result1 = new TrainingRunResult
            {
                RunId = runId1,
                Success = true,
                OnnxArtifactPath = onnxFile,
                MetricsArtifactPath = metricsFile,
            };

            var service = new ModelDeploymentService();
            var promoted1 = await service.PromoteTempRunToTrainingArtifactsAsync(result1);

            Assert.Contains(runId1, promoted1.OnnxArtifactPath);
            Assert.True(File.Exists(promoted1.OnnxArtifactPath));
            Assert.Equal("onnx-run1", await File.ReadAllTextAsync(promoted1.OnnxArtifactPath));
            Assert.True(File.Exists(promoted1.MetricsArtifactPath!));

            var destDir1 = Path.GetDirectoryName(promoted1.OnnxArtifactPath)!;
            Assert.False(File.Exists(Path.Combine(destDir1, "unrelated_notes.txt")));
            Assert.False(File.Exists(Path.Combine(destDir1, "random_data.json")));

            // Now promote a second run with same filename "model.onnx" but different RunId
            var runId2 = "run-default-2";
            var result2 = new TrainingRunResult
            {
                RunId = runId2,
                Success = true,
                OnnxArtifactPath = onnxFile,
                MetricsArtifactPath = metricsFile,
            };

            var promoted2 = await service.PromoteTempRunToTrainingArtifactsAsync(result2);
            Assert.Contains(runId2, promoted2.OnnxArtifactPath);
            Assert.NotEqual(promoted1.OnnxArtifactPath, promoted2.OnnxArtifactPath);
            Assert.True(File.Exists(promoted1.OnnxArtifactPath));
            Assert.True(File.Exists(promoted2.OnnxArtifactPath));

            try
            {
                Directory.Delete(destDir1, recursive: true);
                Directory.Delete(Path.GetDirectoryName(promoted2.OnnxArtifactPath)!, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }

        [Fact]
        public async Task PromoteTempRunToTrainingArtifactsAsync_RollbackOnCancellation_PreservesExistingDestinationFiles()
        {
            var targetDir = Path.Combine(Path.GetTempPath(), "sa_rollback_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(targetDir);
            try
            {
                var existingOnnx = Path.Combine(targetDir, "model.onnx");
                await File.WriteAllTextAsync(existingOnnx, "original-protected-content");

                var sourceOnnx = WriteSourceFile("model.onnx", "new-temp-content");
                var result = new TrainingRunResult
                {
                    RunId = "run-cancelled",
                    Success = true,
                    OnnxArtifactPath = sourceOnnx,
                };

                using var cts = new CancellationTokenSource();
                cts.Cancel(); // cancelled immediately

                var service = new ModelDeploymentService();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    service.PromoteTempRunToTrainingArtifactsAsync(result, targetDir, cts.Token));

                // Original file in destination was preserved and NOT corrupted or deleted
                Assert.True(File.Exists(existingOnnx));
                Assert.Equal("original-protected-content", await File.ReadAllTextAsync(existingOnnx));
            }
            finally
            {
                try { Directory.Delete(targetDir, recursive: true); } catch { /* best effort */ }
            }
        }

        private string WriteSourceFile(string fileName, string content)
        {
            var path = Path.Combine(_sourceDir, fileName);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
