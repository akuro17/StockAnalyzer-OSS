using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Default implementation of IExecutionContext.
/// </summary>
public class CoreExecutionContext : IExecutionContext
{
    public IPythonService? PythonService { get; }

    public CoreExecutionContext(IPythonService? pythonService = null)
    {
        PythonService = pythonService;
    }
}
