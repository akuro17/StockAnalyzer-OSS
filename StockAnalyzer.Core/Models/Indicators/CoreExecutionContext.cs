using System.Threading;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Default implementation of IExecutionContext.
/// </summary>
public class CoreExecutionContext : ICancellableExecutionContext
{
    public IPythonService? PythonService { get; }

    public CancellationToken CancellationToken { get; }

    public CoreExecutionContext(IPythonService? pythonService = null)
    {
        PythonService = pythonService;
        CancellationToken = CancellationToken.None;
    }

    public CoreExecutionContext(IPythonService? pythonService, CancellationToken cancellationToken)
    {
        PythonService = pythonService;
        CancellationToken = cancellationToken;
    }
}
