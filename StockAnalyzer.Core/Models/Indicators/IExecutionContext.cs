using System;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Provides contextual services and contextual information for indicator execution.
/// </summary>
public interface IExecutionContext
{
    /// <summary>
    /// Gets the service required for Python interop, if configured.
    /// </summary>
    IPythonService? PythonService { get; }
}
