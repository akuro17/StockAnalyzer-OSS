#nullable enable
using System;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A fact that runs heavy child process tests (e.g. starting external Python training pipelines)
/// only when the opt-in environment variable <c>SA_ENABLE_CHILD_PROCESS_TESTS=1</c> is set.
/// When unset, the test is cleanly reported as skipped, never as a silent pass or a multi-minute hang.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ChildProcessFactAttribute : FactAttribute
{
    public const string EnabledVariable = "SA_ENABLE_CHILD_PROCESS_TESTS";

    public ChildProcessFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
        {
            Skip = $"Opt-in heavy child process test (starts external Python processes); set {EnabledVariable}=1 to run.";
        }
    }
}

/// <summary>
/// A theory that runs heavy child process tests (e.g. starting external Python training pipelines)
/// only when the opt-in environment variable <c>SA_ENABLE_CHILD_PROCESS_TESTS=1</c> is set.
/// When unset, the test is cleanly reported as skipped, never as a silent pass or a multi-minute hang.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ChildProcessTheoryAttribute : TheoryAttribute
{
    public const string EnabledVariable = "SA_ENABLE_CHILD_PROCESS_TESTS";

    public ChildProcessTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
        {
            Skip = $"Opt-in heavy child process test (starts external Python processes); set {EnabledVariable}=1 to run.";
        }
    }
}
