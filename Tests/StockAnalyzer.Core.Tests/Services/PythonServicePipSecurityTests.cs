using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PythonServicePipSecurityTests
    {
        [Fact]
        public void PipSecurityUpgradeArguments_EnforcesMinimumPipVersionAndCertifi()
        {
            Assert.Equal($"-m pip install --upgrade \"pip>={PythonService.MinimumPipVersion}\" certifi", PythonService.PipSecurityUpgradeArguments);
        }

        [Fact]
        public void MinimumPipVersion_IsAtLeastSecurityRelease()
        {
            Assert.True(System.Version.Parse(PythonService.MinimumPipVersion) >= new System.Version(26, 2, 2));
        }
    }
}
