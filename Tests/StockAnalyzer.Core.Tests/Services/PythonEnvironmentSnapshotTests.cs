using System.Collections.Generic;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PythonEnvironmentSnapshotTests
    {
        [Fact]
        public void Parse_ValidPayload_MapsInterpreterAndPackageVersions()
        {
            const string json = """
            {"python": "3.13.1", "packages": {"pandas": "2.2.2", "yfinance": "0.2.40", "arch": null}}
            """;

            var snapshot = PythonEnvironmentSnapshot.Parse(json);

            Assert.True(snapshot.InterpreterInstalled);
            Assert.Equal("3.13.1", snapshot.PythonVersion);
            Assert.Equal("2.2.2", snapshot.PackageVersions["pandas"]);
            Assert.Equal("0.2.40", snapshot.PackageVersions["yfinance"]);
            Assert.Null(snapshot.PackageVersions["arch"]);
        }

        [Fact]
        public void Parse_PackageLookupIsCaseInsensitive()
        {
            const string json = """{"python": "3.13.1", "packages": {"scikit-learn": "1.4.2"}}""";

            var snapshot = PythonEnvironmentSnapshot.Parse(json);

            Assert.Equal("1.4.2", snapshot.PackageVersions["SciKit-Learn"]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("not json at all")]
        [InlineData("{\"packages\": {\"pandas\": \"2.2.2\"}}")] // missing "python"
        [InlineData("{\"python\": \"\", \"packages\": {}}")]     // blank "python"
        [InlineData("[1, 2, 3]")]                                  // wrong root kind
        public void Parse_InvalidOrIncompletePayload_ReturnsNotInstalled(string? json)
        {
            var snapshot = PythonEnvironmentSnapshot.Parse(json);

            Assert.Same(PythonEnvironmentSnapshot.NotInstalled, snapshot);
            Assert.False(snapshot.InterpreterInstalled);
            Assert.Null(snapshot.PythonVersion);
            Assert.Empty(snapshot.PackageVersions);
        }

        [Fact]
        public void Parse_MissingPackagesObject_YieldsEmptyMapButInstalledInterpreter()
        {
            const string json = """{"python": "3.13.1"}""";

            var snapshot = PythonEnvironmentSnapshot.Parse(json);

            Assert.True(snapshot.InterpreterInstalled);
            Assert.Equal("3.13.1", snapshot.PythonVersion);
            Assert.Empty(snapshot.PackageVersions);
        }

        [Fact]
        public void NotInstalled_IsStableSingletonWithEmptyState()
        {
            var a = PythonEnvironmentSnapshot.NotInstalled;
            var b = PythonEnvironmentSnapshot.NotInstalled;

            Assert.Same(a, b);
            Assert.False(a.InterpreterInstalled);
            Assert.Empty(a.PackageVersions);
        }
    }
}
