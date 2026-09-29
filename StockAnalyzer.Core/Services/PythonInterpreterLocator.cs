using System;
using System.IO;
using System.Runtime.InteropServices;
using Python.Included;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Resolves the absolute path of the embedded CPython interpreter managed by
    /// <c>Python.Included</c> <b>without triggering installation</b>. Shared by components that
    /// only need to read the local environment (currently <see cref="PythonEnvironmentInspector"/>).
    /// <para>
    /// Mirrors the discovery layout historically inlined inside <see cref="PythonService"/>: the
    /// interpreter is either directly under <see cref="Installer.InstallPath"/> or inside a
    /// <c>python-*-embed-*</c> sub-directory produced by the embeddable-zip extraction. The inlined
    /// copies in <see cref="PythonService"/> are intentionally left untouched here; consolidating
    /// them is a separate improvement.
    /// </para>
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

            string root;
            try
            {
                root = Installer.InstallPath;
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var direct = Path.Combine(root, ExecutableName);
            if (File.Exists(direct))
            {
                executablePath = direct;
                return true;
            }

            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root, "python-*-embed-*"))
                {
                    var nested = Path.Combine(dir, ExecutableName);
                    if (File.Exists(nested))
                    {
                        executablePath = nested;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
