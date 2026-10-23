using System;
using System.Diagnostics;
using System.IO;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>
    /// Runs the content of <see cref="PythonEmbeddedDistribution.ScriptPathFileName"/> the way <c>site</c> does and checks which
    /// <c>sys.argv[0]</c> values put their folder on <c>sys.path</c>. Needs the embedded Python; without it the tests are reported as skipped.
    /// </summary>
    public class PythonScriptPathContentTests : IDisposable
    {
        private const string Harness =
            "import sys; pth, target = sys.argv[1], sys.argv[2]; before = list(sys.path); sys.argv = [target]; " +
            "exec(open(pth).read()); print(sys.path[0] if sys.path != before else '')";

        private const int ProcessTimeoutMilliseconds = 30_000;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_pth_" + Guid.NewGuid().ToString("N"));

        public PythonScriptPathContentTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [EmbeddedPythonTheory]
        [InlineData("tool.py", true)]
        [InlineData("tool.PYW", true)]
        [InlineData("tool.exe", false)]
        [InlineData("notes.txt", false)]
        public void ScriptPathContent_InsertsFolderOnlyForPythonScripts(string fileName, bool expectInserted)
        {
            var folder = Path.Combine(_root, "dir_" + Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant());
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, fileName);
            File.WriteAllText(target, string.Empty);
            var pth = Path.Combine(_root, PythonEmbeddedDistribution.ScriptPathFileName);
            File.WriteAllText(pth, PythonEmbeddedDistribution.ScriptPathContent);

            Assert.Equal(expectInserted ? Path.GetFullPath(folder) : string.Empty, RunPython(pth, target));
        }

        private static string RunPython(string pth, string target)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = PythonEmbeddedInstaller.ExecutablePath(PythonEmbeddedDistribution.PythonExecutableName),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(Harness);
            startInfo.ArgumentList.Add(pth);
            startInfo.ArgumentList.Add(target);

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(ProcessTimeoutMilliseconds))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                Assert.Fail("the interpreter did not finish");
            }

            Assert.True(process.ExitCode == 0, error);
            return output;
        }
    }
}
