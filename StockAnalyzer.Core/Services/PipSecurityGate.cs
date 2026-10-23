using System;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Makes sure the embedded pip is at least <see cref="PythonService.MinimumPipVersion"/> once per process. The installed
    /// version is read first; the upgrade (which also refreshes certifi) runs only when that version is missing or lower.
    /// A failed attempt is not remembered, so the next call tries again.
    /// </summary>
    internal sealed class PipSecurityGate
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private volatile bool _secured;

        /// <param name="readInstalledVersion">Returns the installed pip version, or null when it cannot be read.</param>
        /// <param name="upgrade">Runs the security upgrade; throws when it fails.</param>
        public async Task EnsureAsync(
            Func<CancellationToken, Task<string?>> readInstalledVersion,
            Func<CancellationToken, Task> upgrade,
            CancellationToken ct)
        {
            if (_secured) return;

            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_secured) return;

                string? installed = await readInstalledVersion(ct).ConfigureAwait(false);
                if (!IsAtLeastMinimum(installed))
                {
                    await upgrade(ct).ConfigureAwait(false);
                }

                _secured = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static bool IsAtLeastMinimum(string? installed)
            => System.Version.TryParse(installed, out System.Version? version)
                && version >= new System.Version(PythonService.MinimumPipVersion);
    }
}
