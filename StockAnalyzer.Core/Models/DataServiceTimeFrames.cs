using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// The single definition of the time frames the data service can serve: D1 from the stored daily files; W1 and MN1 from the stored Weekly and Monthly
/// files or aggregated from the daily bars, depending on the host. The data layer does not support the intraday frames. Hosts that need a list of supported time frames (the read-only gateway, the backtest, scatter
/// metrics) expose this list under their own name instead of restating it.
/// </summary>
public static class DataServiceTimeFrames
{
    /// <summary>The supported time frames in display order: Day, Week, Month. Immutable.</summary>
    public static IReadOnlyList<TimeFrame> Supported { get; } = Array.AsReadOnly(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 });
}
