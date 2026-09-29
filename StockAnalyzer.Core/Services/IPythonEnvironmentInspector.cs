using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Read-only probe of the local Python environment used by informational surfaces such as the
    /// "Settings / About" page. Implementations MUST NOT trigger a Python download, extraction or
    /// <c>pip install</c>: an absent interpreter is a valid result, not something to remediate.
    /// </summary>
    public interface IPythonEnvironmentInspector
    {
        /// <summary>
        /// Inspects the embedded interpreter and resolves the installed version of each supplied
        /// distribution name (as understood by <c>importlib.metadata.version</c>). Never throws:
        /// any failure (no interpreter, timeout, malformed output) resolves to
        /// <see cref="PythonEnvironmentSnapshot.NotInstalled"/>.
        /// </summary>
        Task<PythonEnvironmentSnapshot> InspectAsync(
            IReadOnlyCollection<string> distributionNames,
            CancellationToken ct = default);
    }
}
