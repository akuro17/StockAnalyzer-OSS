using System;
using System.IO;
using Xunit;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PythonScriptLocatorTests
    {
        [Fact]
        public void Resolve_UpdatePipelineScript_ReturnsExistingFile()
        {
            var resolved = PythonScriptLocator.Resolve("update_pipeline.py");

            Assert.True(File.Exists(resolved), $"Resolved path does not exist: {resolved}");
            Assert.Equal("update_pipeline.py", Path.GetFileName(resolved));
        }

        [Fact]
        public void Resolve_NestedScriptWithBackslashes_NormalizesAndReturnsExistingFile()
        {
            // Windows-style separators must resolve the same as forward slashes, since callers
            // (and the training orchestrator) may build the relative path either way.
            var resolved = PythonScriptLocator.Resolve(@"training\run_training.py");

            Assert.True(File.Exists(resolved), $"Resolved path does not exist: {resolved}");
            Assert.Equal("run_training.py", Path.GetFileName(resolved));
        }

        [Fact]
        public void Resolve_UnknownScript_ThrowsFileNotFoundException()
        {
            Assert.Throws<FileNotFoundException>(
                () => PythonScriptLocator.Resolve("this_script_does_not_exist_12345.py"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_NullOrBlankPath_ThrowsArgumentException(string? relativeScriptPath)
        {
            Assert.Throws<ArgumentException>(() => PythonScriptLocator.Resolve(relativeScriptPath!));
        }
    }
}
