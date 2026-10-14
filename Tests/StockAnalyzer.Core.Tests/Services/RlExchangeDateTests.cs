using System;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The exchange date decides which stored bar is "today" (never a completed bar). It must follow the exchange's local midnight, also around the
/// changes of daylight saving time, and must not depend on the time zone of the machine.
/// </summary>
public sealed class RlExchangeDateTests
{
    private const string NewYork = "America/New_York";
    private const string Tokyo = "Asia/Tokyo";

    // New York: DST starts 2026-03-08 02:00 local (UTC-5 -> UTC-4) and ends 2026-11-01 02:00 local (UTC-4 -> UTC-5).
    [Theory]
    [InlineData(NewYork, "2026-03-08T04:59:59Z", "2026-03-07")] // 23:59:59 on 7 March (UTC-5)
    [InlineData(NewYork, "2026-03-08T05:00:00Z", "2026-03-08")] // local midnight of the day DST starts (UTC-5)
    [InlineData(NewYork, "2026-03-09T03:59:59Z", "2026-03-08")] // 23:59:59 on 8 March (UTC-4: the first midnight after the change)
    [InlineData(NewYork, "2026-03-09T04:00:00Z", "2026-03-09")]
    [InlineData(NewYork, "2026-11-01T03:59:59Z", "2026-10-31")] // 23:59:59 on 31 October (UTC-4)
    [InlineData(NewYork, "2026-11-01T04:00:00Z", "2026-11-01")] // local midnight of the day DST ends (UTC-4)
    [InlineData(NewYork, "2026-11-02T04:59:59Z", "2026-11-01")] // 23:59:59 on 1 November (UTC-5: the first midnight after the change)
    [InlineData(NewYork, "2026-11-02T05:00:00Z", "2026-11-02")]
    [InlineData(Tokyo, "2026-03-08T14:59:59Z", "2026-03-08")] // UTC+9, no DST
    [InlineData(Tokyo, "2026-03-08T15:00:00Z", "2026-03-09")]
    [InlineData(Tokyo, "2026-11-01T14:59:59Z", "2026-11-01")]
    [InlineData(Tokyo, "2026-11-01T15:00:00Z", "2026-11-02")]
    public void TheExchangeDateFollowsTheLocalMidnightOfTheExchangeZone(string zoneId, string instant, string expected)
    {
        if (!TryFindZone(zoneId, out var zone)) return; // The host has no time-zone data for this id; there is nothing to verify here.
        Assert.Equal(DateOnly.Parse(expected), RlLocalSnapshotFactory.ExchangeDate(zone, DateTimeOffset.Parse(instant, null, System.Globalization.DateTimeStyles.AssumeUniversal)));
    }

    [Fact]
    public void TheSameInstantGivesTheSameExchangeDateWhateverOffsetItIsExpressedIn()
    {
        if (!TryFindZone(Tokyo, out var zone)) return;
        var utc = new DateTimeOffset(2026, 3, 8, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(RlLocalSnapshotFactory.ExchangeDate(zone, utc), RlLocalSnapshotFactory.ExchangeDate(zone, utc.ToOffset(TimeSpan.FromHours(-8))));
    }

    private static bool TryFindZone(string id, out TimeZoneInfo zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; return false; }
    }
}
