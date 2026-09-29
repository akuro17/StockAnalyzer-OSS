using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Python.Included;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Default <see cref="IPythonEnvironmentInspector"/>. Spawns the embedded interpreter exactly
    /// once per call and reads every requested distribution version through
    /// <c>importlib.metadata</c>. The probe script is delivered over stdin (<c>python -</c>) to
    /// avoid command-line quoting and temp-file management. Stateless, safe to register as a
    /// singleton.
    /// </summary>
    public sealed class PythonEnvironmentInspector : IPythonEnvironmentInspector
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        // Placeholder replaced with a JSON array literal of the requested distribution names.
        private const string ProbeScriptTemplate = @"
import json, platform
try:
    import importlib.metadata as _md
except Exception:
    _md = None

_names = __NAMES__
_out = {""python"": platform.python_version(), ""packages"": {}}
for _n in _names:
    _v = None
    if _md is not None:
        try:
            _v = _md.version(_n)
        except Exception:
            _v = None
    _out[""packages""][_n] = _v
print(json.dumps(_out))
";

        private readonly ILogger<PythonEnvironmentInspector> _logger;

        public PythonEnvironmentInspector(ILogger<PythonEnvironmentInspector>? logger = null)
        {
            _logger = logger ?? NullLogger<PythonEnvironmentInspector>.Instance;
        }

        public async Task<PythonEnvironmentSnapshot> InspectAsync(
            IReadOnlyCollection<string> distributionNames,
            CancellationToken ct = default)
        {
            var names = (distributionNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Never provoke a Python setup from an informational page: bail out quietly when the
            // interpreter is not already present.
            bool interpreterPresent;
            try
            {
                interpreterPresent = Installer.IsPythonInstalled();
            }
            catch
            {
                interpreterPresent = false;
            }

            if (!interpreterPresent || !PythonInterpreterLocator.TryResolveExecutable(out var pythonExe))
            {
                return PythonEnvironmentSnapshot.NotInstalled;
            }

            var script = ProbeScriptTemplate.Replace("__NAMES__", JsonSerializer.Serialize(names));

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "-",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = startInfo };

                using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                await using var kill = linkedCts.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
                });

                if (!process.Start())
                {
                    return PythonEnvironmentSnapshot.NotInstalled;
                }

                await process.StandardInput.WriteAsync(script).ConfigureAwait(false);
                process.StandardInput.Close();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    var stderr = await stderrTask.ConfigureAwait(false);
                    _logger.LogWarning(
                        "Python environment inspection exited with code {ExitCode}: {Error}",
                        process.ExitCode, stderr);
                    return PythonEnvironmentSnapshot.NotInstalled;
                }

                return PythonEnvironmentSnapshot.Parse(stdout);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Python environment inspection failed; reporting as not installed.");
                return PythonEnvironmentSnapshot.NotInstalled;
            }
        }
    }
}
