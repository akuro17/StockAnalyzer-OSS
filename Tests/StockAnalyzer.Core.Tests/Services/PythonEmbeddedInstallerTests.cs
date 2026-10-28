using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PythonEmbeddedInstallerTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sa-python-embed-tests", Guid.NewGuid().ToString("N"));

        public PythonEmbeddedInstallerTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public void FolderName_IsTheExactPinnedFolder()
        {
            Assert.Equal("python-3.13.17-embed-amd64", PythonEmbeddedDistribution.FolderName);
        }

        [Fact]
        public void PthContent_ListsStandardLibraryZipFolderAndSitePackagesAndImportsSite()
        {
            Assert.Equal("python313.zip\n.\nLib\\site-packages\nimport site\n", PythonEmbeddedDistribution.PthContent);
        }

        [Fact]
        public void VerifySha256_DifferentContent_Throws()
        {
            var path = Path.Combine(_root, "archive.zip");
            File.WriteAllText(path, "not the python.org archive");

            Assert.Throws<InvalidDataException>(() =>
                PythonEmbeddedInstaller.VerifySha256(path, PythonEmbeddedDistribution.ArchiveSha256));
        }

        [Fact]
        public void GetPipSha256_Is64HexCharacters()
        {
            Assert.NotNull(PythonEmbeddedDistribution.GetPipSha256);
            Assert.Equal(64, PythonEmbeddedDistribution.GetPipSha256.Length);
            Assert.StartsWith("https://", PythonEmbeddedDistribution.GetPipUrl, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void VerifySha256_GetPip_DifferentContent_Throws()
        {
            var path = Path.Combine(_root, "get-pip.py");
            File.WriteAllText(path, "tampered get-pip script");

            Assert.Throws<InvalidDataException>(() =>
                PythonEmbeddedInstaller.VerifySha256(path, PythonEmbeddedDistribution.GetPipSha256));
        }

        [Fact]
        public void VerifySha256_MatchingHash_DoesNotThrow()
        {
            var path = Path.Combine(_root, "archive.zip");
            var bytes = Encoding.UTF8.GetBytes("known content");
            File.WriteAllBytes(path, bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            PythonEmbeddedInstaller.VerifySha256(path, expected);
        }

        [Fact]
        public void PrepareLayout_WritesPathFileAndCreatesSitePackages()
        {
            var directory = Path.Combine(_root, "install");
            Directory.CreateDirectory(directory);

            PythonEmbeddedInstaller.PrepareLayout(directory);

            Assert.Equal(PythonEmbeddedDistribution.PthContent, File.ReadAllText(Path.Combine(directory, PythonEmbeddedDistribution.PthFileName)));
            Assert.True(Directory.Exists(Path.Combine(directory, "Lib", "site-packages")));
        }

        [Fact]
        public void PrepareLayout_WritesScriptPathFileIntoSitePackages()
        {
            var directory = Path.Combine(_root, "install");
            Directory.CreateDirectory(directory);

            PythonEmbeddedInstaller.PrepareLayout(directory);

            var scriptPath = Path.Combine(directory, "Lib", "site-packages", PythonEmbeddedDistribution.ScriptPathFileName);
            Assert.Equal(PythonEmbeddedDistribution.ScriptPathContent, File.ReadAllText(scriptPath));
        }

        [Fact]
        public void PrepareLayout_RunTwice_KeepsContent()
        {
            var directory = Path.Combine(_root, "install");
            Directory.CreateDirectory(directory);

            PythonEmbeddedInstaller.PrepareLayout(directory);
            PythonEmbeddedInstaller.PrepareLayout(directory);

            Assert.Equal(PythonEmbeddedDistribution.PthContent, File.ReadAllText(Path.Combine(directory, PythonEmbeddedDistribution.PthFileName)));
        }

        [Fact]
        public void IsInstalledIn_WithoutStamp_ReturnsFalse()
        {
            var directory = CreateInterpreterFolder();

            Assert.False(PythonEmbeddedInstaller.IsInstalledIn(directory));
        }

        [Fact]
        public void IsInstalledIn_StampWithOtherVersion_ReturnsFalse()
        {
            var directory = CreateInterpreterFolder();
            File.WriteAllText(Path.Combine(directory, PythonEmbeddedDistribution.StampFileName), "3.13.0");

            Assert.False(PythonEmbeddedInstaller.IsInstalledIn(directory));
        }

        [Fact]
        public void IsInstalledIn_StampWithPinnedVersion_ReturnsTrue()
        {
            var directory = CreateInterpreterFolder();
            File.WriteAllText(Path.Combine(directory, PythonEmbeddedDistribution.StampFileName), PythonEmbeddedDistribution.PythonVersion);

            Assert.True(PythonEmbeddedInstaller.IsInstalledIn(directory));
        }

        private string CreateInterpreterFolder()
        {
            var directory = Path.Combine(_root, PythonEmbeddedDistribution.FolderName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, PythonEmbeddedDistribution.PythonExecutableName), string.Empty);
            return directory;
        }
    }
}
