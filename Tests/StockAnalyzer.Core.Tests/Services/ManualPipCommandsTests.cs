using System;
using System.Linq;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class ManualPipCommandsTests
    {
        private static string Interpreter => PythonEmbeddedInstaller.ExecutablePath("python.exe");

        private static string InterpreterQuoted => $"\"{Interpreter}\"";

        private static string InterpreterSingleQuoted => $"'{Interpreter.Replace("'", "''")}'";

        private const string ExitCodeGuard = "; if ($LASTEXITCODE -eq 0) { ";

        [Fact]
        public void PowerShell7_UsesSingleQuotedCallOperatorAndDoubleAmpersandBetweenSteps()
        {
            var command = ManualPipCommands.Setup(ManualShell.PowerShell7);

            Assert.StartsWith($"& {InterpreterSingleQuoted} -m pip install --upgrade \"pip>={PythonService.MinimumPipVersion}\" certifi && & {InterpreterSingleQuoted} -m pip install", command);
            Assert.Contains(" && ", command);
        }

        // D-14 B: Windows PowerShell 5.1 has no "&&"; each later step runs only when the previous one ended with exit code 0.
        [Fact]
        public void WindowsPowerShell51_RunsALaterStepOnlyAfterASuccessfulPreviousStep()
        {
            var command = ManualPipCommands.Setup(ManualShell.WindowsPowerShell);

            Assert.StartsWith($"& {InterpreterSingleQuoted} -m pip install", command);
            Assert.DoesNotContain(" && ", command);
            Assert.DoesNotContain("exit", command);
            Assert.Contains($"{ExitCodeGuard}& {InterpreterSingleQuoted} -m pip install", command);
            Assert.EndsWith(" }", command);
        }

        [Fact]
        public void WindowsPowerShell51_NestsEveryFurtherStepInsideThePreviousGuard()
        {
            var command = ManualPipCommands.Build(ManualShell.WindowsPowerShell, new[] { "first", "second", "third" });
            var step = $"& {InterpreterSingleQuoted}";

            Assert.Equal(
                $"{step} first{ExitCodeGuard}{step} second{ExitCodeGuard}{step} third }} }}",
                command);
        }

        [Fact]
        public void SingleStep_HasNoGuard()
        {
            var command = ManualPipCommands.Build(ManualShell.WindowsPowerShell, new[] { "only" });

            Assert.Equal($"& {InterpreterSingleQuoted} only", command);
        }

        [Fact]
        public void CommandPrompt_HasNoCallOperatorAndUsesDoubleAmpersand()
        {
            var command = ManualPipCommands.Setup(ManualShell.CommandPrompt);

            Assert.StartsWith($"{InterpreterQuoted} -m pip install", command);
            Assert.DoesNotMatch(@"(?<!&)& """, command);
            Assert.Contains($" && {InterpreterQuoted} -m pip install", command);
        }

        // D-15 B: a PowerShell double-quoted path expands $name, so PowerShell paths use single quotes and double a single quote.
        [Fact]
        public void QuotePath_PowerShellUsesSingleQuotesAndDoublesEmbeddedSingleQuotes()
        {
            const string path = @"C:\Users\TestUser\a$b'c\python.exe";

            Assert.Equal(@"'C:\Users\TestUser\a$b''c\python.exe'", ManualPipCommands.QuotePath(ManualShell.PowerShell7, path));
            Assert.Equal(@"'C:\Users\TestUser\a$b''c\python.exe'", ManualPipCommands.QuotePath(ManualShell.WindowsPowerShell, path));
        }

        [Fact]
        public void QuotePath_CommandPromptKeepsDoubleQuotes()
        {
            Assert.Equal("\"C:" + @"\Users\TestUser\a$b'c\python.exe" + "\"", ManualPipCommands.QuotePath(ManualShell.CommandPrompt, @"C:\Users\TestUser\a$b'c\python.exe"));
        }

        [Fact]
        public void Interpreter_IsTheAbsolutePathOfTheEmbeddedPython()
        {
            var expected = PythonEmbeddedInstaller.ExecutablePath("python.exe");

            Assert.True(System.IO.Path.IsPathRooted(ManualPipCommands.Interpreter));
            Assert.Equal(expected, ManualPipCommands.Interpreter);
            Assert.EndsWith(PythonEmbeddedDistribution.FolderName + "\\python.exe", ManualPipCommands.Interpreter, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RequirementsFile_IsAnAbsolutePath()
        {
            Assert.True(System.IO.Path.IsPathRooted(ManualPipCommands.RequirementsFile));
            Assert.EndsWith("requirements.txt", ManualPipCommands.RequirementsFile, StringComparison.Ordinal);
        }

        [Fact]
        public void Setup_InstallsRequirementsAtPinnedVersionsWithoutUpgrade()
        {
            var command = ManualPipCommands.Setup(ManualShell.CommandPrompt);

            Assert.Contains($"-m pip install --no-build-isolation -r \"{ManualPipCommands.RequirementsFile}\"", command);
            Assert.DoesNotContain("--upgrade -r", command);
        }

        [Theory]
        [InlineData(ManualShell.PowerShell7)]
        [InlineData(ManualShell.WindowsPowerShell)]
        public void Setup_PowerShellQuotesTheRequirementsPathWithSingleQuotes(ManualShell shell)
        {
            var command = ManualPipCommands.Setup(shell);

            Assert.Contains($"-m pip install --no-build-isolation -r {ManualPipCommands.QuotePath(shell, ManualPipCommands.RequirementsFile)}", command);
            Assert.DoesNotContain($"-r \"{ManualPipCommands.RequirementsFile}\"", command);
        }

        [Fact]
        public void Update_InstallsLatestEssentialPackagesWithoutPins()
        {
            var command = ManualPipCommands.Update(ManualShell.PowerShell7);

            Assert.Contains("-m pip install --no-build-isolation --upgrade yfinance pandas polars pyarrow", command);
            Assert.DoesNotContain("==", command);
        }

        [Fact]
        public void Onnx_InstallsTorchFromCpuIndexAndTheOtherPackagesFromDefaultIndex()
        {
            var lines = ManualPipCommands.OnnxArguments(upgrade: false);

            Assert.Equal(2, lines.Count);
            Assert.Contains($"--index-url {PythonPackageSets.TorchIndexUrl} {PythonPackageSets.TorchPackage}", lines[0]);
            Assert.DoesNotContain("--index-url", lines[1]);
            foreach (var package in PythonPackageSets.OnnxTraining.Where(p => p != PythonPackageSets.TorchPackage))
            {
                Assert.Contains(package, lines[1].Split(' '));
            }
        }

        [Fact]
        public void Onnx_UpgradeAddsUpgradeToEveryStep()
        {
            var lines = ManualPipCommands.OnnxArguments(upgrade: true);

            Assert.All(lines, line => Assert.Contains(" --upgrade", line));
        }

        [Fact]
        public void Onnx_CommandStartsWithSecurityUpgradeOfPip()
        {
            var command = ManualPipCommands.Onnx(ManualShell.PowerShell7, upgrade: false);

            Assert.StartsWith($"& {InterpreterSingleQuoted} {ManualPipCommands.SecurityUpgradeArguments} && ", command);
        }

        [Fact]
        public void AllShells_ShowsOneLabelledBlockPerShell()
        {
            var text = ManualPipCommands.AllShells(ManualPipCommands.Update);

            Assert.Contains("PowerShell 7:", text);
            Assert.Contains("Windows PowerShell 5.1:", text);
            Assert.Contains("Command Prompt (cmd):", text);
            Assert.Equal(3, text.Split('\n').Count(line => line.EndsWith(':')));
        }
    }
}
