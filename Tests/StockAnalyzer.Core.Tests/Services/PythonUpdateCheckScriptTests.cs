using System;
using System.Diagnostics;
using System.IO;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>
    /// Runs <see cref="PythonService.UpdateCheckScript"/> in the embedded interpreter with the installed version and the PyPI answer replaced
    /// by fixed values, so the range rule is checked offline. Without the embedded interpreter the tests are reported as skipped.
    /// </summary>
    public class PythonUpdateCheckScriptTests
    {
        private const string UpdateAvailable = "UPDATE_AVAILABLE";
        private const string NoUpdate = "NO_UPDATE";

        // 2.3.5 is yanked (all files), 3.1.0rc1 and 2.4.0rc1 are pre-releases, 3.1.0 is the global latest.
        private const string PypiAnswer =
            "{'info': {'version': '3.1.0'}, 'releases': {" +
            "'2.2.0': [{}], '2.3.3': [{}], '2.3.4': [{}], '2.3.5': [{'yanked': True}], '2.4.0rc1': [{}], " +
            "'3.0.0': [{}], '3.1.0rc1': [{}], '3.1.0': [{}], '1.0': [{}], '1.0.0': [{}], '0.9.0': []}}";

        [EmbeddedPythonTheory]
        [InlineData("2.3.3", "2.3.3", 3, UpdateAvailable)]
        // REV-03 reproduction: the global latest (3.1.0) is newer, but nothing newer exists inside the range.
        [InlineData("2.3.4", "2.3.3", 3, NoUpdate)]
        // Installed above the range (user updated to 3.x): never a downgrade.
        [InlineData("3.0.0", "2.3.3", 3, NoUpdate)]
        // Installed below the pin: update to the latest inside the range.
        [InlineData("2.2.0", "2.3.3", 3, UpdateAvailable)]
        // Trailing zeros are not a difference.
        [InlineData("1.0", "1.0", 2, NoUpdate)]
        public void InsideRange_ComparesWithTheLatestReleaseOfTheRange(string installed, string pin, int upperMajor, string expected)
        {
            Assert.Equal(expected, RunScript(installed, pin, upperMajor));
        }

        // N-04 A: without a pin the comparison is the global latest, as before.
        [EmbeddedPythonFact]
        public void WithoutRange_ComparesWithTheGlobalLatest()
        {
            Assert.Equal(UpdateAvailable, RunScript("2.3.4", pin: null, upperMajor: null));
        }

        private static string RunScript(string installed, string? pin, int? upperMajor)
        {
            var pythonExe = PythonEmbeddedInstaller.ExecutablePath(PythonEmbeddedDistribution.PythonExecutableName);

            var stub =
                "import importlib.metadata, io, json, urllib.request\n" +
                $"_answer = {PypiAnswer}\n" +
                "class _Response(io.BytesIO):\n" +
                "    def __enter__(self): return self\n" +
                "    def __exit__(self, *args): return False\n" +
                "urllib.request.urlopen = lambda *a, **k: _Response(json.dumps(_answer).encode())\n" +
                $"importlib.metadata.version = lambda name: '{installed}'\n";

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-");
            startInfo.ArgumentList.Add("pandas");
            startInfo.ArgumentList.Add(new StockAnalyzer.Core.Models.Settings.PythonPackageCheckSettings().UpdateCheckRequestTimeoutSeconds.ToString());
            if (pin is not null && upperMajor is not null)
            {
                startInfo.ArgumentList.Add(pin);
                startInfo.ArgumentList.Add(upperMajor.Value.ToString());
            }

            using var process = Process.Start(startInfo)!;
            process.StandardInput.Write(stub + PythonService.UpdateCheckScript);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "the script did not finish");
            Assert.True(process.ExitCode == 0, error);
            return output;
        }
    }
}
