// ==================================================================================
// FILE: StockAnalyzer/Models/TimeframeType.cs
// ==================================================================================
namespace StockAnalyzer.Core.Models
{
    /// <summary>
    /// Defines supported chart timeframes.
    /// Replaces string-based management to ensure type safety across the application.
    /// </summary>
    public enum TimeframeType
    {
        M1,     // 1 Minute
        M5,     // 5 Minutes
        M15,    // 15 Minutes
        H1,     // 1 Hour
        H4,     // 4 Hours
        Daily,  // Daily
        Weekly,  // Weekly
        Monthly  // Monthly
    }
}
