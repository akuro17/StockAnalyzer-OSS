#nullable enable
using System;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>A fact that runs only when the named environment variable is set; otherwise it is reported as skipped, never as a silent pass.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable))) Skip = $"Measurement harness: set {variable} to run it.";
    }
}
