using System;
using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Tests.E2E
{
    /// <summary>
    /// Base class for E2E integration tests using FlaUI.
    /// Handles application launch, cleanup, and common UI automation helpers.
    /// </summary>
    public abstract class E2ETestBase : IDisposable
    {
        protected Application? App { get; private set; }
        protected UIA3Automation? Automation { get; private set; }
        protected Window? MainWindow { get; private set; }

        private const int APP_LAUNCH_TIMEOUT_MS = 10000;
        private const int ELEMENT_WAIT_TIMEOUT_MS = 5000;

        // Same path the real app resolves via WorkspaceCoordinatorOptions.WorkspacePath.
        // Backed up before launch and restored in Dispose() so E2E runs never depend on -
        // or overwrite - the developer's real workspace, and never inherit a stray detached
        // window left over from manual use, which would otherwise race with GetMainWindow()
        // for the OS main window handle.
        private readonly string _workspaceFilePath = PathDiscovery.ResolveConfigPath(StockAnalyzer.Core.Constants.LayoutConstants.DefaultWorkspaceFileName);
        private string? _workspaceBackupPath;

        protected E2ETestBase()
        {
            // FlaUI setup
            Automation = new UIA3Automation();
        }

        /// <summary>
        /// Launches the StockAnalyzer application and waits for main window.
        /// </summary>
        protected void LaunchApplication()
        {
            BackupWorkspaceFile();

            // Find the application executable
            string appPath = FindApplicationPath();
            
            if (!File.Exists(appPath))
            {
                throw new FileNotFoundException($"Application not found at: {appPath}");
            }

            // Launch the application
            App = Application.Launch(appPath);
            
            // Wait for main window to appear
            var result = App.WaitWhileMainHandleIsMissing(TimeSpan.FromMilliseconds(APP_LAUNCH_TIMEOUT_MS));
            
            if (!result)
            {
                throw new TimeoutException("Application failed to launch within timeout period");
            }

            // Get main window
            MainWindow = App.GetMainWindow(Automation);
            
            if (MainWindow == null)
            {
                throw new InvalidOperationException("Failed to get main window");
            }

            // Additional wait for window to be ready
            System.Threading.Thread.Sleep(1000);
        }

        /// <summary>
        /// Moves the developer's local workspace settings file aside (if present) so the
        /// launched app starts from a clean default layout instead of restoring detached
        /// windows left over from manual use. Skips silently if a backup from a prior
        /// interrupted run is already present, to avoid clobbering it.
        /// </summary>
        private void BackupWorkspaceFile()
        {
            var backupPath = _workspaceFilePath + ".e2e_backup";
            if (File.Exists(_workspaceFilePath) && !File.Exists(backupPath))
            {
                File.Move(_workspaceFilePath, backupPath);
                _workspaceBackupPath = backupPath;
            }
        }

        /// <summary>
        /// Restores the workspace settings file backed up by <see cref="BackupWorkspaceFile"/>,
        /// if any. Best-effort: never throws, so cleanup always completes.
        /// </summary>
        private void RestoreWorkspaceFile()
        {
            if (_workspaceBackupPath == null) return;

            try
            {
                if (File.Exists(_workspaceBackupPath))
                {
                    if (File.Exists(_workspaceFilePath))
                    {
                        File.Delete(_workspaceFilePath);
                    }
                    File.Move(_workspaceBackupPath, _workspaceFilePath);
                }
            }
            catch
            {
                // Best-effort restore; do not fail test cleanup on I/O contention.
            }
            finally
            {
                _workspaceBackupPath = null;
            }
        }

        /// <summary>
        /// Finds the StockAnalyzer application executable path.
        /// </summary>
        private string FindApplicationPath()
        {
            // Try different possible locations
            var basePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));

            var possiblePaths = new System.Collections.Generic.List<string>();

            // Allows developers whose build output lives outside the repo-relative
            // locations below to point at it without hardcoding a machine-specific path.
            var envOverride = Environment.GetEnvironmentVariable("SA_E2E_APP_PATH");
            if (!string.IsNullOrWhiteSpace(envOverride))
            {
                possiblePaths.Add(envOverride);
            }

            possiblePaths.Add(Path.Combine(basePath, "StockAnalyzer.Avalonia", "bin", "Debug", "net8.0", "StockAnalyzer.Avalonia.exe"));
            possiblePaths.Add(Path.Combine(basePath, "StockAnalyzer.Avalonia", "bin", "Release", "net8.0", "StockAnalyzer.Avalonia.exe"));

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new FileNotFoundException("Could not find StockAnalyzer.exe in any expected location");
        }

        /// <summary>
        /// Waits for an element to appear with specified automation ID.
        /// </summary>
        protected AutomationElement? WaitForElement(string automationId, int timeoutMs = ELEMENT_WAIT_TIMEOUT_MS)
        {
            if (MainWindow == null) return null;

            var endTime = DateTime.Now.AddMilliseconds(timeoutMs);
            
            while (DateTime.Now < endTime)
            {
                try
                {
                    var element = MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                    if (element != null)
                    {
                        return element;
                    }
                }
                catch
                {
                    // Element not found yet, continue waiting
                }

                System.Threading.Thread.Sleep(100);
            }

            return null;
        }

        /// <summary>
        /// Closes the application and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            try
            {
                App?.Close();
                App?.Dispose();
            }
            catch
            {
                // Force kill if graceful close fails
                try
                {
                    if (App?.ProcessId != null)
                    {
                        var process = Process.GetProcessById(App.ProcessId);
                        process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort cleanup, but a failed kill can leave StockAnalyzer.Avalonia.exe
                    // running and locking its own build output for subsequent builds/launches.
                    Trace.WriteLine($"[E2ETestBase] Failed to force-kill leaked process (PID {App?.ProcessId}): {ex}");
                }
            }
            
            Automation?.Dispose();

            RestoreWorkspaceFile();
        }
    }
}
