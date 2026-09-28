using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Common;
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

        private string WriteSourceFile(string fileName, string content)
        {
            var path = Path.Combine(_sourceDir, fileName);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
