using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace StockAnalyzer.Core.Services
{
    /// <summary>Shell that a manual pip command is written for.</summary>
    public enum ManualShell
    {
        /// <summary>PowerShell 7 (pwsh): <c>&amp;</c> call operator and <c>&amp;&amp;</c> between steps.</summary>
        PowerShell7,

        /// <summary>
        /// Windows PowerShell 5.1: <c>&amp;</c> call operator (no <c>&amp;&amp;</c>); each later step is nested in
        /// <c>if ($LASTEXITCODE -eq 0) { ... }</c> so it runs only after a successful previous step.
        /// </summary>
        WindowsPowerShell,

        /// <summary>cmd.exe: no call operator and <c>&amp;&amp;</c> between steps.</summary>
        CommandPrompt
    }

    /// <summary>
    /// Builds the manual pip commands shown to the user (setup, update, ONNX). On Windows the commands name
    /// the embedded interpreter by its absolute path, so they update the Python the app uses. On other
    /// operating systems they use <c>python3</c> from the PATH.
    /// </summary>
    public static class ManualPipCommands
    {
        private const string NonWindowsInterpreter = "python3";

        /// <summary>
        /// Shell form of every command copied to the clipboard. Windows PowerShell 5.1 (the default <c>powershell</c> window) has no <c>&amp;&amp;</c>; its form
        /// (steps nested in <c>if ($LASTEXITCODE -eq 0) { ... }</c>) is also valid in PowerShell 7, so one pasted command works in both.
        /// </summary>
        public const ManualShell ClipboardShell = ManualShell.WindowsPowerShell;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>Absolute path of the interpreter the app uses (Windows), or <c>python3</c> elsewhere.</summary>
        public static string Interpreter =>
            IsWindows
                ? PythonEmbeddedInstaller.ExecutablePath(PythonEmbeddedDistribution.PythonExecutableName)
                : NonWindowsInterpreter;

        /// <summary>
        /// Absolute path of <c>requirements.txt</c> in the installed application folder. Falls back to the
        /// published layout beside the executable when the file cannot be located.
        /// </summary>
        public static string RequirementsFile
        {
            get
            {
                try
                {
                    return PythonScriptLocator.Resolve("requirements.txt");
                }
                catch (FileNotFoundException)
                {
                    return Path.Combine(AppContext.BaseDirectory, PythonScriptLocator.PythonRootFolderName, "requirements.txt");
                }
            }
        }

        /// <summary>pip arguments that upgrade pip and certifi (same as the automatic path).</summary>
        public static string SecurityUpgradeArguments => PythonService.PipSecurityUpgradeArguments;

        /// <summary>Setup: installs requirements.txt at the pinned versions (fresh install).</summary>
        public static string SetupArguments => $"{PythonPackageSets.PipInstallPrefix} -r \"{RequirementsFile}\"";

        /// <summary>Update: installs the latest version of the essential packages.</summary>
        public static string UpdateArguments =>
            $"{PythonPackageSets.PipInstallPrefix} --upgrade " + string.Join(' ', PythonPackageSets.Update);

        /// <summary>ONNX training packages: torch from the CPU index, the others from the default index.</summary>
        public static IReadOnlyList<string> OnnxArguments(bool upgrade)
        {
            var upgradeFlag = upgrade ? " --upgrade" : string.Empty;
            var others = PythonPackageSets.OnnxTraining.Where(p => p != PythonPackageSets.TorchPackage);
            return new[]
            {
                $"{PythonPackageSets.PipInstallPrefix}{upgradeFlag} --index-url {PythonPackageSets.TorchIndexUrl} {PythonPackageSets.TorchPackage}",
                $"{PythonPackageSets.PipInstallPrefix}{upgradeFlag} {string.Join(' ', others)}"
            };
        }

        /// <summary>Setup arguments with the requirements path quoted for <paramref name="shell"/> (single quotes in PowerShell, so <c>$</c> is not expanded).</summary>
        internal static string SetupArgumentsFor(ManualShell shell) =>
            IsWindows && shell != ManualShell.CommandPrompt
                ? $"{PythonPackageSets.PipInstallPrefix} -r {QuotePath(shell, RequirementsFile)}"
                : SetupArguments;

        /// <summary>
        /// <paramref name="path"/> as one argument for <paramref name="shell"/>. PowerShell: single quotes with an embedded <c>'</c> doubled (a
        /// double-quoted string expands <c>$name</c>). cmd: double quotes.
        /// </summary>
        internal static string QuotePath(ManualShell shell, string path) =>
            shell == ManualShell.CommandPrompt
                ? $"\"{path}\""
                : $"'{path.Replace("'", "''")}'";

        /// <summary>
        /// Joins pip argument lists into one command for <paramref name="shell"/>. PowerShell 7 and cmd: <c>&amp;&amp;</c>. Windows PowerShell 5.1 has no
        /// <c>&amp;&amp;</c> and a <c>;</c> would run a later step after a failed one, so each later step is nested in an exit-code check (no
        /// <c>exit</c>: pasted into an interactive session it would close the session).
        /// </summary>
        public static string Build(ManualShell shell, IEnumerable<string> pipArguments)
        {
            var steps = pipArguments.Select(arguments => Step(shell, arguments)).ToList();
            return shell == ManualShell.WindowsPowerShell
                ? NestOnSuccess(steps, 0)
                : string.Join(" && ", steps);
        }

        private const string RunNextOnSuccess = "; if ($LASTEXITCODE -eq 0) { ";

        private static string NestOnSuccess(IReadOnlyList<string> steps, int index)
        {
            if (steps.Count == 0) return string.Empty;
            return index == steps.Count - 1
                ? steps[index]
                : $"{steps[index]}{RunNextOnSuccess}{NestOnSuccess(steps, index + 1)} }}";
        }

        /// <summary>Setup command: security upgrade of pip, then requirements.txt.</summary>
        public static string Setup(ManualShell shell) =>
            Build(shell, new[] { SecurityUpgradeArguments, SetupArgumentsFor(shell) });

        /// <summary>Update command: security upgrade of pip, then the latest essential packages.</summary>
        public static string Update(ManualShell shell) =>
            Build(shell, new[] { SecurityUpgradeArguments, UpdateArguments });

        /// <summary>ONNX command: security upgrade of pip, then the ONNX training packages.</summary>
        public static string Onnx(ManualShell shell, bool upgrade) =>
            Build(shell, new[] { SecurityUpgradeArguments }.Concat(OnnxArguments(upgrade)));

        /// <summary>
        /// The command for the three shells, one labelled block each, for the instruction dialogs.
        /// </summary>
        public static string AllShells(Func<ManualShell, string> build)
        {
            var blocks = new[]
            {
                ("PowerShell 7:", ManualShell.PowerShell7),
                ("Windows PowerShell 5.1:", ManualShell.WindowsPowerShell),
                ("Command Prompt (cmd):", ManualShell.CommandPrompt)
            };
            return string.Join("\n", blocks.Select(b => $"{b.Item1}\n   {build(b.Item2)}"));
        }

        private static string Step(ManualShell shell, string arguments)
        {
            if (!IsWindows)
            {
                return $"{NonWindowsInterpreter} {arguments}";
            }

            var quoted = QuotePath(shell, Interpreter);
            return shell == ManualShell.CommandPrompt
                ? $"{quoted} {arguments}"
                : $"& {quoted} {arguments}";
        }
    }
}
