// --------------------------------------------------------------------------------
// File: Infrastructure/UiTestWatchdog.cs
// --------------------------------------------------------------------------------
using System;
using System.Threading;

namespace StockAnalyzer.UITests.Infrastructure
{
    /// <summary>
    /// One-shot timer that runs <c>onExpired</c> on a thread-pool thread when the limit passes before the watchdog is disposed.
    /// The action is injected so the timing rule can be tested without terminating the test host.
    /// </summary>
    public sealed class UiTestWatchdog : IDisposable
    {
        private readonly Timer _timer;

        public UiTestWatchdog(TimeSpan limit, Action onExpired)
        {
            ArgumentNullException.ThrowIfNull(onExpired);
            _timer = new Timer(_ => onExpired(), null, limit, Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => _timer.Dispose();
    }
}
