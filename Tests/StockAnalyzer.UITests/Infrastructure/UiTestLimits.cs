// --------------------------------------------------------------------------------
// File: Infrastructure/UiTestLimits.cs
// --------------------------------------------------------------------------------
namespace StockAnalyzer.UITests.Infrastructure
{
    /// <summary>
    /// Single definition of the wall-clock limit of one UI test (fixture preparation, launch and body).
    /// A test that runs longer is a timeout and its test process is terminated (see <see cref="UiTestWatchdog"/>).
    /// </summary>
    public static class UiTestLimits
    {
        /// <summary>Five minutes. xUnit's own <c>Timeout</c> cannot interrupt a synchronous test body, so the watchdog enforces this value.</summary>
        public const int TestTimeLimitMs = 300_000;
    }
}
