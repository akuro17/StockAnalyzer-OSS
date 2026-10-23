#nullable enable
using System;
using System.IO;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Facts and theories that need a tool of the machine (Windows, the application's embedded Python). When the tool is missing they are reported as
/// skipped with the reason, never as a silent pass: a test that returns before its first assertion proves nothing.
/// </summary>
internal static class ToolAvailability
{
    public const string WindowsRequired = "Windows only: the test starts cmd.exe.";

    public const string EmbeddedPythonRequired = "Needs the application's embedded Python (PythonEmbeddedInstaller.ExecutablePath) to be installed.";

    public static bool IsEmbeddedPythonInstalled
        => File.Exists(StockAnalyzer.Core.Services.PythonEmbeddedInstaller.ExecutablePath(StockAnalyzer.Core.Services.PythonEmbeddedDistribution.PythonExecutableName));
}

/// <summary>A fact that runs only on Windows (it starts cmd.exe); otherwise it is skipped.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = ToolAvailability.WindowsRequired;
    }
}

/// <summary>A fact that runs only when the embedded Python is installed; otherwise it is skipped.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class EmbeddedPythonFactAttribute : FactAttribute
{
    public EmbeddedPythonFactAttribute()
    {
        if (!ToolAvailability.IsEmbeddedPythonInstalled) Skip = ToolAvailability.EmbeddedPythonRequired;
    }
}

/// <summary>A theory that runs only when the embedded Python is installed; otherwise it is skipped.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class EmbeddedPythonTheoryAttribute : TheoryAttribute
{
    public EmbeddedPythonTheoryAttribute()
    {
        if (!ToolAvailability.IsEmbeddedPythonInstalled) Skip = ToolAvailability.EmbeddedPythonRequired;
    }
}
