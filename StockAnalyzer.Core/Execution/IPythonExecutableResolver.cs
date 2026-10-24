using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// Locates an installed Python interpreter without installing or initialising anything. A caller that must not provoke a Python setup (the
/// sandboxed execution gateway) uses it instead of <c>IPythonService.ResolvePythonExecutablePathAsync</c>.
/// </summary>
public interface IPythonExecutableResolver
{
    /// <summary>True and the interpreter's path when one is installed; false (never an exception) otherwise.</summary>
    bool TryResolve(out string executablePath);
}

/// <summary>The embedded interpreter the application installs (<see cref="PythonInterpreterLocator.TryResolveExecutable"/>).</summary>
public sealed class EmbeddedPythonExecutableResolver : IPythonExecutableResolver
{
    public bool TryResolve(out string executablePath) => PythonInterpreterLocator.TryResolveExecutable(out executablePath);
}
