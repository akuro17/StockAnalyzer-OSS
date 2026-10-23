using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PythonServiceInstallArgumentsTests
    {
        private const string Install = "-m pip install --no-build-isolation";

        // The security upgrade (pip, certifi) shares the command start but is not a package build: it must not carry the build-isolation switch.
        [Fact]
        public void SecurityUpgrade_SharesTheCommandStartWithoutTheBuildIsolationSwitch()
        {
            Assert.Equal($"-m pip install --upgrade \"pip>={PythonService.MinimumPipVersion}\" certifi", PythonService.PipSecurityUpgradeArguments);
            Assert.StartsWith(PythonPackageSets.PipInstallCommand + " ", PythonService.PipSecurityUpgradeArguments);
            Assert.DoesNotContain("--no-build-isolation", PythonService.PipSecurityUpgradeArguments);
            Assert.StartsWith(PythonPackageSets.PipInstallCommand + " ", PythonPackageSets.PipInstallPrefix);
        }

        [Fact]
        public void FreshInstallOfPinnedPackage_UsesExactPin()
        {
            Assert.Equal($"{Install} pandas==2.3.3", PythonService.BuildInstallArguments("pandas", "2.3.3", shouldUpdate: false, forceUpgrade: false));
        }

        [Fact]
        public void AutomaticUpdateOfPinnedPackage_StaysInsideTheMajor()
        {
            Assert.Equal($"{Install} --upgrade \"pandas>=2.3.3,<3.0.0\"", PythonService.BuildInstallArguments("pandas", "2.3.3", shouldUpdate: true, forceUpgrade: false));
            Assert.Equal($"{Install} --upgrade \"yfinance>=1.0,<2.0.0\"", PythonService.BuildInstallArguments("yfinance", "1.0", shouldUpdate: true, forceUpgrade: false));
        }

        // N-04 A: a user-initiated forced upgrade stays unbounded.
        [Fact]
        public void ForcedUpgradeOfPinnedPackage_IsUnbounded()
        {
            Assert.Equal($"{Install} --upgrade pandas", PythonService.BuildInstallArguments("pandas", "2.3.3", shouldUpdate: true, forceUpgrade: true));
        }

        // N-04 A: a package without a pin has no range to stay in.
        [Fact]
        public void UnpinnedPackage_KeepsTheCurrentArguments()
        {
            Assert.Equal($"{Install} numpy", PythonService.BuildInstallArguments("numpy", null, shouldUpdate: false, forceUpgrade: false));
            Assert.Equal($"{Install} --upgrade numpy", PythonService.BuildInstallArguments("numpy", null, shouldUpdate: true, forceUpgrade: false));
        }

        [Fact]
        public void Torch_KeepsTheCpuIndexArguments()
        {
            var index = PythonPackageSets.TorchIndexUrl;
            Assert.Equal($"{Install} torch --index-url {index}", PythonService.BuildInstallArguments("torch", null, shouldUpdate: false, forceUpgrade: false));
            Assert.Equal($"{Install} --upgrade torch --index-url {index}", PythonService.BuildInstallArguments("torch", null, shouldUpdate: true, forceUpgrade: false));
        }
    }
}
