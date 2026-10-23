using System;
using System.IO;
using System.Runtime.InteropServices;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Resolves the absolute path of the pinned embedded CPython interpreter <b>without triggering
    /// installation</b>. Shared by components that only need to read the local environment (currently
    /// <see cref="PythonEnvironmentInspector"/>). The interpreter is looked up only in the exact folder
    /// <see cref="PythonEmbeddedDistribution.FolderName"/> (see <see cref="PythonEmbeddedInstaller"/>).
    /// </summary>
    public static class PythonInterpreterLocator
    {
        /// <summary>Platform-specific interpreter file name.</summary>
        public static string ExecutableName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python.exe" : "python3";

        /// <summary>
        /// Attempts to locate the embedded interpreter executable. Returns <c>false</c> (rather than
        /// throwing) whenever the install root is unavailable or no interpreter is present, so
        /// callers can degrade gracefully without provoking a Python setup.
        /// </summary>
        public static bool TryResolveExecutable(out string executablePath)
        {
            executablePath = string.Empty;

            string candidate;
            try
            {
                candidate = PythonEmbeddedInstaller.ExecutablePath(ExecutableName);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(candidate))
            {
                return false;
            }

            executablePath = candidate;
            return true;
        }
    }
}
