using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Python.Runtime;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Installs and locates the embedded CPython interpreter that the app uses on Windows. The
    /// version, URL and hash come from <see cref="PythonEmbeddedDistribution"/>. Only the exact
    /// folder <see cref="PythonEmbeddedDistribution.FolderName"/> is used; the install never touches
    /// any other Python folder. On other operating systems every method is a no-op.
    /// </summary>
    public static class PythonEmbeddedInstaller
    {
        private const string GetPipScriptName = "get-pip.py";

        private static readonly SemaphoreSlim SetupLock = new(1, 1);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        /// <summary>Folder that holds the embedded interpreter (%LOCALAPPDATA% on Windows).</summary>
        public static string InstallRoot
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root))
                {
                    throw new InvalidOperationException("The local application data folder is not available.");
                }

                return root;
            }
        }

        /// <summary>Exact folder of the pinned interpreter.</summary>
        public static string InstallDirectory => Path.Combine(InstallRoot, PythonEmbeddedDistribution.FolderName);

        /// <summary>Path of <paramref name="exeName"/> inside the pinned folder. The file may not exist.</summary>
        public static string ExecutablePath(string exeName) => Path.Combine(InstallDirectory, exeName);

        /// <summary>True when the pinned folder holds a complete install (the stamp is written last).</summary>
        public static bool IsInstalled() => IsInstalledIn(InstallDirectory);

        internal static bool IsInstalledIn(string directory)
        {
            var exe = Path.Combine(directory, PythonEmbeddedDistribution.PythonExecutableName);
            var stamp = Path.Combine(directory, PythonEmbeddedDistribution.StampFileName);
            return File.Exists(exe)
                && File.Exists(stamp)
                && string.Equals(File.ReadAllText(stamp).Trim(), PythonEmbeddedDistribution.PythonVersion, StringComparison.Ordinal);
        }

        /// <summary>
        /// Points pythonnet at the pinned interpreter. Must run before <see cref="PythonEngine.Initialize()"/>.
        /// </summary>
        public static void ConfigureEngine()
        {
            if (!IsWindows())
            {
                return;
            }

            Runtime.PythonDLL = Path.Combine(InstallDirectory, PythonEmbeddedDistribution.PythonDllName);
            PythonEngine.PythonHome = InstallDirectory;
        }

        /// <summary>
        /// Installs the pinned interpreter when it is not installed yet. On failure nothing is left in the
        /// pinned folder and the error is thrown (no fallback to another Python).
        /// </summary>
        public static async Task SetupAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            if (!IsWindows())
            {
                return;
            }

            await SetupLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsInstalled())
                {
                    // Installs made before the script-path file existed get it here; no download.
                    PrepareLayout(InstallDirectory);
                    return;
                }

                var finalDirectory = InstallDirectory;
                if (Directory.Exists(finalDirectory))
                {
                    throw new InvalidOperationException(
                        $"{finalDirectory} exists but is not a complete install. Remove that folder by hand and start again.");
                }

                var partialDirectory = finalDirectory + PythonEmbeddedDistribution.PartialSuffix;
                var archivePath = partialDirectory + PythonEmbeddedDistribution.ArchiveExtension;
                DeleteFileIfExists(archivePath);
                DeleteDirectoryIfExists(partialDirectory);

                try
                {
                    progress?.Report($"Downloading Python {PythonEmbeddedDistribution.PythonVersion}...");
                    await DownloadAsync(PythonEmbeddedDistribution.ArchiveUrl, archivePath, ct).ConfigureAwait(false);
                    VerifySha256(archivePath, PythonEmbeddedDistribution.ArchiveSha256);

                    progress?.Report("Extracting Python environment...");
                    ZipFile.ExtractToDirectory(archivePath, partialDirectory);
                    PrepareLayout(partialDirectory);

                    progress?.Report("Installing pip...");
                    await BootstrapPipAsync(partialDirectory, ct).ConfigureAwait(false);

                    File.WriteAllText(
                        Path.Combine(partialDirectory, PythonEmbeddedDistribution.StampFileName),
                        PythonEmbeddedDistribution.PythonVersion);
                    Directory.Move(partialDirectory, finalDirectory);
                }
                catch
                {
                    DeleteDirectoryIfExists(partialDirectory);
                    throw;
                }
                finally
                {
                    DeleteFileIfExists(archivePath);
                }
            }
            finally
            {
                SetupLock.Release();
            }
        }

        /// <summary>Compares the SHA-256 of <paramref name="path"/> with <paramref name="expectedSha256"/>.</summary>
        internal static void VerifySha256(string path, string expectedSha256)
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-256 mismatch for {Path.GetFileName(path)}: expected {expectedSha256}, got {actual}.");
            }
        }

        /// <summary>
        /// Writes the layout files: the path file (ignores the registry PythonPath), the site file that restores
        /// the script folder on sys.path, and site-packages. Safe to run on an existing install; a file is
        /// rewritten only when its content differs.
        /// </summary>
        internal static void PrepareLayout(string directory)
        {
            var sitePackages = Path.Combine(directory, PythonEmbeddedDistribution.SitePackagesRelativePath);
            Directory.CreateDirectory(sitePackages);
            WriteIfChanged(Path.Combine(directory, PythonEmbeddedDistribution.PthFileName), PythonEmbeddedDistribution.PthContent);
            WriteIfChanged(Path.Combine(sitePackages, PythonEmbeddedDistribution.ScriptPathFileName), PythonEmbeddedDistribution.ScriptPathContent);
        }

        private static void WriteIfChanged(string path, string content)
        {
            if (!File.Exists(path) || File.ReadAllText(path) != content)
            {
                File.WriteAllText(path, content);
            }
        }

        private static async Task BootstrapPipAsync(string directory, CancellationToken ct)
        {
            var scriptPath = Path.Combine(directory, GetPipScriptName);
            await DownloadAsync(PythonEmbeddedDistribution.GetPipUrl, scriptPath, ct).ConfigureAwait(false);
            VerifySha256(scriptPath, PythonEmbeddedDistribution.GetPipSha256);
            try
            {
                await RunGetPipAsync(directory, ct).ConfigureAwait(false);
            }
            finally
            {
                DeleteFileIfExists(scriptPath);
            }
        }

        private static async Task RunGetPipAsync(string directory, CancellationToken ct)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(directory, PythonEmbeddedDistribution.PythonExecutableName),
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(GetPipScriptName);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the embedded Python for pip.");
            using var kill = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"get-pip.py failed with exit code {process.ExitCode}: {stderr}");
            }
        }

        private static async Task DownloadAsync(string url, string destination, CancellationToken ct)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(destination);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }
}
