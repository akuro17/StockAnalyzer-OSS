// --------------------------------------------------------------------------------
// File: Infrastructure/QualifiedEvidenceFactAttribute.cs
// --------------------------------------------------------------------------------
using System;
using System.Linq;
using Xunit;

namespace StockAnalyzer.UITests.Infrastructure
{
    /// <summary>
    /// A UI test that evaluates results of long real-data training runs (calculation validity) and therefore needs
    /// externally produced evidence folders. It is outside the basic-behaviour UI phase: skipped unless every required
    /// environment variable is set.
    /// </summary>
    public sealed class QualifiedEvidenceFactAttribute : FactAttribute
    {
        public QualifiedEvidenceFactAttribute(params string[] requiredVariables)
        {
            string[] missing = requiredVariables
                .Where(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                .ToArray();
            if (missing.Length > 0)
                Skip = "Outside the basic UI phase (calculation evaluation on qualified real-data runs); set " + string.Join(", ", missing) + " to run it.";
        }
    }
}
