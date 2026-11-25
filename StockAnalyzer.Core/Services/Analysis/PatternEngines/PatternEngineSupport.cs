using System;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>What the pattern engines of the chart-pattern indicators have in common beyond the pure functions of <see cref="PatternMath"/>: the direction filter, the depth rule of the formations and the bar guard of the output.</summary>
public static class PatternEngineSupport
{
    /// <summary>True when the direction mode lets a bullish pattern through (plan R-13: the filter narrows the candidates BEFORE any tie-break).</summary>
    public static bool AllowsBullish(PatternDirectionMode mode) => mode != PatternDirectionMode.BearishOnly;

    /// <summary>True when the direction mode lets a bearish pattern through.</summary>
    public static bool AllowsBearish(PatternDirectionMode mode) => mode != PatternDirectionMode.BullishOnly;

    /// <summary>
    /// Spec 1.1: a bar that is not OHLC-consistent gives "not met" (0) at that bar. The events of an engine are made of consistent bars only, but a hold period can run over an inconsistent bar, so the
    /// result of such a bar is cleared here (plan R-14, interpretation I-5 of ledger V2-3). The event itself is not ended by the bad bar.
    /// </summary>
    public static void ClearInconsistentBars(PatternSeriesContext ctx, PatternBarResult[] results)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(results);
        for (int i = 0; i < results.Length && i < ctx.Count; i++)
        {
            if (!ctx.IsValid(i))
            {
                results[i] = default;
            }
        }
    }

    /// <summary>
    /// The depth rule shared by Head and Shoulders (spec 01 section 4) and Double Top / Bottom (spec 02 section 4): <c>depth / reference &gt;= minPercent / 100</c>, or
    /// (<c>depth &gt;= 2.5 ATR_ctx(atrBar, 14)</c> and <c>depth / reference &gt;= 0.02</c>). The ATR alternative is not met when the ATR cannot be computed.
    /// </summary>
    public static bool NecklineDepthMet(PatternSeriesContext ctx, decimal depth, decimal referencePrice, decimal minPercent, int atrBar)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (depth <= 0m || referencePrice <= 0m)
        {
            return false;
        }

        decimal ratio = depth / referencePrice;
        if (ratio >= minPercent / 100m)
        {
            return true;
        }

        return ratio >= PatternSpecConstants.FormationDepthAtrFloorFraction
               && PatternMath.TryAtrCtx(ctx, atrBar, PatternSpecConstants.AtrPeriod, out decimal atr, out _)
               && depth >= PatternSpecConstants.FormationDepthAtrMultiplier * atr;
    }
}
