// --------------------------------------------------------------------------------
// File: Infrastructure/UITestBase.cs
// --------------------------------------------------------------------------------
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using FlaUI.Core.Definitions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly string _screenshotDirectory;
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

        private const string EnableEnvVar = "SA_UI_TESTS_ENABLED";

        protected void LaunchApplication()
        {
            // These tests launch and maximize a real StockAnalyzer.Avalonia window (some for up to
            // 20 minutes), which steals OS focus/foreground from whatever else is running. Require an
            // explicit opt-in so an accidental direct invocation of this project (e.g. running
            // `dotnet test` from inside Tests\StockAnalyzer.UITests) doesn't hijack the developer's screen.
            if (Environment.GetEnvironmentVariable(EnableEnvVar) != "1")
            {
                throw new InvalidOperationException(
                    $"UI automation tests are disabled by default because they launch and maximize a real " +
                    $"application window for an extended period, interfering with other running applications. " +
                    $"Set the environment variable {EnableEnvVar}=1 to run them intentionally.");
            }

            string appPath = FindApplicationPath();
            LogInfo($"Launching application from: {appPath}");

            Automation = new UIA3Automation();
            App = Application.Launch(appPath);

            // Wait for the main window handle using retry logic
            var sw = Stopwatch.StartNew();
            var mainWindowResult = Retry.WhileNull(
                () => App.GetMainWindow(Automation),
                TimeSpan.FromMilliseconds(StartupTimeoutMs),
                TimeSpan.FromMilliseconds(DefaultPollingIntervalMs),
                throwOnTimeout: true,
                timeoutMessage: $"Main window not found within {StartupTimeoutMs}ms"
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

            // Tier 2: Search in standard build output directories using iterative approach
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
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
            }
            _disposed = true;
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
