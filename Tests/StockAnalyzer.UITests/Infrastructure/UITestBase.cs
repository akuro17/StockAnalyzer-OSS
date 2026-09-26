// --------------------------------------------------------------------------------
// File: Infrastructure/UITestBase.cs
// --------------------------------------------------------------------------------
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using FlaUI.Core.Definitions;
using StockAnalyzer.Core.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Infrastructure
{
    /// <summary>
    /// Base class for UI automation tests using FlaUI.
    /// Implements tiered application discovery, robust lifecycle management, 
    /// and condition-based synchronization (No Thread.Sleep).
    /// </summary>
    public abstract class UITestBase : IDisposable
    {
        protected Application? App { get; private set; }
        public UIA3Automation? Automation { get; private set; }
        protected Window? MainWindow { get; private set; }
        protected ITestOutputHelper? Output { get; }

        private const string AppExecutableName = "StockAnalyzer.Avalonia.exe";
        private const string IsolatedRunsDirectoryName = "UITestRuns";
        private const string OwnedRootMarkerName = ".sa-uia-owned";
        private readonly string _screenshotDirectory;
        private string? _isolatedDataRoot;
        private string? _isolatedRunsRoot;
        private bool _disposed;

        // Configurable timeouts via Environment Variables (CI flexibility)
        private static readonly int StartupTimeoutMs = GetEnvInt("UI_TEST_STARTUP_TIMEOUT", 15000);
        private static readonly int DefaultWaitTimeoutMs = GetEnvInt("UI_TEST_DEFAULT_TIMEOUT", 5000);
        private static readonly int DefaultPollingIntervalMs = GetEnvInt("UI_TEST_POLLING_INTERVAL", 100);

        protected UITestBase(ITestOutputHelper? output = null)
        {
            Output = output;
            _screenshotDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots");
            Directory.CreateDirectory(_screenshotDirectory);
        }

        protected void LaunchApplication()
        {
            // These tests launch and maximize a real StockAnalyzer.Avalonia window (some for up to
            // 20 minutes), which steals OS focus/foreground from whatever else is running. Require an
            // explicit opt-in so an accidental direct invocation of this project (e.g. running
            // `dotnet test` from inside Tests\StockAnalyzer.UITests) doesn't hijack the developer's screen.
            if (Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable) != "1")
            {
                throw new InvalidOperationException(
                    $"UI automation tests are disabled by default because they launch and maximize a real " +
                    $"application window for an extended period, interfering with other running applications. " +
                    $"Set the environment variable {PathDiscovery.UiTestEnabledEnvironmentVariable}=1 to run them intentionally.");
            }

            string appPath = FindApplicationPath();
            PrepareIsolatedDataRoot();
            LogInfo($"Launching application from: {appPath}");

            Automation = new UIA3Automation();
            string? previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, _isolatedDataRoot);
                App = Application.Launch(appPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, previousRoot);
            }

            // GetMainWindow enumerates UIA desktop windows while the process is still starting.
            // Wait for this process's native handle first, then bind that handle directly to UIA.
            var sw = Stopwatch.StartNew();
            WaitUntil(
                () => App.HasExited || App.MainWindowHandle != IntPtr.Zero,
                timeoutMs: StartupTimeoutMs,
                errorMessage: $"Process {App.ProcessId} did not create a main window within {StartupTimeoutMs}ms");

            if (App.HasExited)
                throw new InvalidOperationException($"Application exited before creating a main window (PID {App.ProcessId}).");

            var mainWindowResult = Retry.WhileNull(
                () =>
                {
                    try
                    {
                        var handle = App.MainWindowHandle;
                        return handle == IntPtr.Zero ? null : Automation.FromHandle(handle).AsWindow();
                    }
                    catch (COMException)
                    {
                        return null; // UIA provider can lag behind creation of the native window.
                    }
                    catch (TimeoutException)
                    {
                        return null;
                    }
                },
                TimeSpan.FromMilliseconds(StartupTimeoutMs),
                TimeSpan.FromMilliseconds(DefaultPollingIntervalMs),
                throwOnTimeout: true,
                timeoutMessage: $"UIA could not bind the main window of process {App.ProcessId} within {StartupTimeoutMs}ms"
            );

            MainWindow = mainWindowResult.Result;

            // Ensure window is fully interactive
            WaitUntil(
                () => MainWindow != null && MainWindow.IsAvailable && !MainWindow.IsOffscreen,
                timeoutMs: DefaultWaitTimeoutMs,
                errorMessage: "Main window acquired but not interactive (Offscreen or Unavailable)."
            );

            MainWindow!.Focus();
            MainWindow.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
            LogInfo($"Main window acquired in {sw.ElapsedMilliseconds}ms");
        }

        protected string IsolatedDataRoot => _isolatedDataRoot
            ?? throw new InvalidOperationException("LaunchApplication must prepare isolated UIA data first.");

        private void PrepareIsolatedDataRoot()
        {
            var solution = FindSolutionDirectory(AppDomain.CurrentDomain.BaseDirectory)
                ?? throw new DirectoryNotFoundException("Cannot locate solution root for UIA fixture data.");
            string sourceDaily = Path.Combine(solution.FullName, "Data", "Daily");
            string[] parquetFiles = Directory.GetFiles(sourceDaily, "*.parquet");
            if (parquetFiles.Length == 0)
                throw new InvalidOperationException($"No Daily Parquet fixtures found at {sourceDaily}.");

            _isolatedRunsRoot = Path.GetFullPath(Path.Combine(solution.FullName, "Data", IsolatedRunsDirectoryName));
            if ((File.GetAttributes(Path.Combine(solution.FullName, "Data")) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("UIA fixture Data directory must not be a reparse point.");
            Directory.CreateDirectory(_isolatedRunsRoot);
            if ((File.GetAttributes(_isolatedRunsRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("UIA test-runs directory must not be a reparse point.");
            _isolatedDataRoot = Path.Combine(_isolatedRunsRoot, Guid.NewGuid().ToString("N"));
            string isolatedDaily = Path.Combine(_isolatedDataRoot, "Daily");
            Directory.CreateDirectory(isolatedDaily);
            File.WriteAllText(Path.Combine(_isolatedDataRoot, OwnedRootMarkerName), "StockAnalyzer UIA owned data root");
            foreach (string source in parquetFiles)
                File.Copy(source, Path.Combine(isolatedDaily, Path.GetFileName(source)));

            LogInfo($"Isolated UIA data root: {_isolatedDataRoot}; copied {parquetFiles.Length} Daily Parquet fixtures.");
        }

        /// <summary>
        /// Locates the application executable using a tiered strategy:
        /// 1. Environment Variable (CI/CD override)
        /// 2. Iterative manual search in standard build outputs
        /// </summary>
        private string FindApplicationPath()
        {
            // Tier 1: Environment Variable
            var envPath = Environment.GetEnvironmentVariable("STOCK_ANALYZER_PATH");
            if (!string.IsNullOrEmpty(envPath))
            {
                if (!File.Exists(envPath))
                    throw new FileNotFoundException($"ENV STOCK_ANALYZER_PATH invalid: {envPath}");
                if (!envPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"ENV STOCK_ANALYZER_PATH must be an .exe: {envPath}");
                return envPath;
            }

            // Tier 2: Application executable co-located with the running test assembly. MSBuild copies the
            // referenced StockAnalyzer.Avalonia app next to the test binaries for the matching build
            // configuration, so this copy is always in sync with the code under test. Preferring it stops
            // the broad tree walk below from picking up a stale exe left in an unrelated bin folder.
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var coLocatedPath = Path.Combine(baseDir, AppExecutableName);
            if (File.Exists(coLocatedPath))
            {
                LogInfo($"Using co-located application executable: {coLocatedPath}");
                return coLocatedPath;
            }

            // Tier 3: Search in standard build output directories using iterative approach
            var solutionDir = FindSolutionDirectory(baseDir);
            var searchRoot = solutionDir?.FullName ?? baseDir;

            LogInfo($"Searching for {AppExecutableName} in {searchRoot}...");

            var found = ManualSearch(searchRoot, AppExecutableName, maxDepth: 5);
            if (found != null) return found;

            throw new FileNotFoundException(
                $"Could not locate {AppExecutableName}. Please build the project or set STOCK_ANALYZER_PATH.");
        }

        /// <summary>
        /// Iterative depth-limited search with strict filtering for valid build outputs.
        /// Avoids 'obj' folders and ensures the path looks like a binary output.
        /// </summary>
        private string? ManualSearch(string rootPath, string fileName, int maxDepth)
        {
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((rootPath, 0));

            while (stack.Count > 0)
            {
                var (currentPath, depth) = stack.Pop();
                if (depth > maxDepth) continue;

                try
                {
                    // Check files in current directory if it looks like a bin directory
                    if (currentPath.Contains("bin", StringComparison.OrdinalIgnoreCase) &&
                        !currentPath.Contains("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        // Use helper to strictly validate the executable path
                        // This avoids hardcoding "Debug" or "Release" while ensuring we aren't picking up trash
                        var match = Directory.EnumerateFiles(currentPath, fileName)
                            .FirstOrDefault(IsValidBuildOutput);
                        
                        if (match != null) return match;
                    }

                    // Push subdirectories
                    foreach (var dir in Directory.EnumerateDirectories(currentPath))
                    {
                        var name = Path.GetFileName(dir);
                        // Skip system/hidden/reparse points and node_modules/obj
                        if (name.StartsWith(".") || name.Equals("node_modules") || name.Equals("obj")) continue;
                        
                        stack.Push((dir, depth + 1));
                    }
                }
                catch (UnauthorizedAccessException) { /* Ignore */ }
                catch (Exception ex) { LogWarn($"Search error at {currentPath}: {ex.Message}"); }
            }

            return null;
        }

        /// <summary>
        /// Validates that a found executable is in a valid build output directory structure.
        /// Must be in a 'bin' folder (not 'obj') and be an .exe.
        /// </summary>
        private static bool IsValidBuildOutput(string path)
        {
            var dir = Path.GetDirectoryName(path);
            return dir != null &&
                   dir.Contains("bin", StringComparison.OrdinalIgnoreCase) &&
                   !dir.Contains("obj", StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        private DirectoryInfo? FindSolutionDirectory(string startPath)
        {
            var dir = new DirectoryInfo(startPath);
            int sanityCheck = 0;
            while (dir != null && sanityCheck++ < 10)
            {
                if (dir.GetFiles("*.sln").Any()) return dir;
                dir = dir.Parent;
            }
            return null;
        }

        public void CaptureScreenshotOnFailure(string testName)
        {
            try
            {
                if (MainWindow != null && MainWindow.IsAvailable)
                {
                    // Use Guid to prevent filename collisions in fast/parallel failure scenarios
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string guid = Guid.NewGuid().ToString("N")[..8];
                    string safeTestName = Regex.Replace(testName, "[^a-zA-Z0-9_-]", "_");
                    string filename = $"{safeTestName}_{timestamp}_{guid}.png";
                    string filepath = Path.Combine(_screenshotDirectory, filename);

                    var image = FlaUI.Core.Capturing.Capture.Element(MainWindow);
                    image.ToFile(filepath);
                    LogInfo($"Screenshot captured: {filepath}");
                }
            }
            catch (Exception ex)
            {
                LogWarn($"Failed to capture screenshot: {ex.Message}");
            }
        }

        public T WaitForElement<T>(Func<T?> getter, int timeoutMs = -1, string? errorMessage = null) 
            where T : AutomationElement
        {
            timeoutMs = timeoutMs < 0 ? DefaultWaitTimeoutMs : timeoutMs;
            
            var result = Retry.WhileNull(
                getter,
                TimeSpan.FromMilliseconds(timeoutMs),
                TimeSpan.FromMilliseconds(DefaultPollingIntervalMs),
                throwOnTimeout: true,
                timeoutMessage: errorMessage ?? $"Element not found within {timeoutMs}ms"
            );

            return result.Result!;
        }

        public void WaitUntil(Func<bool> condition, int timeoutMs = -1, string? errorMessage = null)
        {
            timeoutMs = timeoutMs < 0 ? DefaultWaitTimeoutMs : timeoutMs;
            Retry.WhileFalse(
                condition,
                TimeSpan.FromMilliseconds(timeoutMs),
                TimeSpan.FromMilliseconds(DefaultPollingIntervalMs),
                throwOnTimeout: true,
                timeoutMessage: errorMessage ?? $"Condition not met within {timeoutMs}ms"
            );
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                try
                {
                    // 1. Close Window gracefully
                    if (MainWindow?.IsAvailable == true)
                    {
                        try 
                        {
                            MainWindow.Close(); 
                        }
                        catch { /* Ignore close errors, proceed to Kill */ }
                    }

                    // 2. Kill Application if still running
                    if (App != null && !App.HasExited)
                    {
                        App.Close();
                        // Verify exit status
                        var exited = Retry.WhileTrue(
                            () => !App.HasExited, 
                            timeout: TimeSpan.FromSeconds(2), 
                            throwOnTimeout: false
                        );

                        if (!exited.Success)
                        {
                            LogWarn("Application stuck. Forcing Kill.");
                            App.Kill();
                        }
                    }

                    Automation?.Dispose();
                }
                catch (Exception ex)
                {
                    // Log error with stack trace (condensed) but DO NOT fail the test.
                    // This prioritizes exposing the original test failure over cleanup issues.
                    LogError($"Dispose error: {ex.Message} (Stack: {ex.StackTrace?.Split('\n')[0]})");
                }
                finally
                {
                    CleanupIsolatedDataRoot();
                }
            }
            _disposed = true;
        }

        private void CleanupIsolatedDataRoot()
        {
            if (_isolatedDataRoot == null || _isolatedRunsRoot == null) return;

            try
            {
                string root = Path.GetFullPath(_isolatedDataRoot);
                string runsRoot = Path.GetFullPath(_isolatedRunsRoot);
                if (!string.Equals(Path.GetDirectoryName(root), runsRoot, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(Path.Combine(root, OwnedRootMarkerName))
                    || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(runsRoot) & FileAttributes.ReparsePoint) != 0
                    || (App != null && !App.HasExited)
                    || Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                {
                    LogWarn($"Preserving unverified or still-in-use UIA data root: {root}");
                    return;
                }

                Directory.Delete(root, recursive: true);
                LogInfo($"Removed test-owned UIA data root: {root}; source Data/Daily remains untouched.");
            }
            catch (Exception ex)
            {
                LogWarn($"Could not safely remove UIA data root {_isolatedDataRoot}: {ex.Message}");
            }
        }

        public void LogInfo(string message) => Output?.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss.fff} {message}");
        public void LogWarn(string message) => Output?.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss.fff} {message}");
        public void LogError(string message) => Output?.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss.fff} {message}");

        private static int GetEnvInt(string key, int defaultValue)
        {
            var val = Environment.GetEnvironmentVariable(key);
            return int.TryParse(val, out int res) ? res : defaultValue;
        }
    }
}
