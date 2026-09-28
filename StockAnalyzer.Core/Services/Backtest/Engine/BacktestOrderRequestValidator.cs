using System;
using System.Globalization;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Submit-boundary validation of a strategy's <see cref="StrategyOrderRequest"/> (the P1 correctness plan T1,
/// finding R8). Before this existed a malformed request was only diagnosed when it reached the fill evaluator on a LATER bar - a request
/// submitted on the last bar, or a GTD order whose ExpiryBar precedes its earliest fill bar, was never diagnosed at all.
/// Only the contracts already stated by <see cref="OrderFillEvaluator"/> and <see cref="TimeInForce"/> are enforced; extraneous prices on an
/// order type that ignores them stay accepted, and a GTC order's unused ExpiryBar is never checked.
/// </summary>
internal static class BacktestOrderRequestValidator
{
    /// <summary>Throws <see cref="ArgumentException"/> identifying the submitted bar, the offending field and the reason.</summary>
    public static void Validate(StrategyOrderRequest request, int submittedBar)
    {
        switch (request.OrderType)
        {
            case OrderType.Market:
                break;
            case OrderType.MarketOnClose:
                if (request.LimitPrice.HasValue) throw Invalid(submittedBar, nameof(request.LimitPrice), "must be null for a MarketOnClose order");
                if (request.StopPrice.HasValue) throw Invalid(submittedBar, nameof(request.StopPrice), "must be null for a MarketOnClose order");
                break;
            case OrderType.Limit:
                if (!request.LimitPrice.HasValue) throw Invalid(submittedBar, nameof(request.LimitPrice), "is required for a Limit order");
                break;
            case OrderType.Stop:
                if (!request.StopPrice.HasValue) throw Invalid(submittedBar, nameof(request.StopPrice), "is required for a Stop order");
                break;
            case OrderType.StopLimit:
                if (!request.LimitPrice.HasValue) throw Invalid(submittedBar, nameof(request.LimitPrice), "is required for a StopLimit order");
                if (!request.StopPrice.HasValue) throw Invalid(submittedBar, nameof(request.StopPrice), "is required for a StopLimit order");
                break;
            default:
                throw Invalid(submittedBar, nameof(request.OrderType), FormattableString.Invariant($"'{request.OrderType}' is not a defined OrderType"));
        }

        int earliestFillBar = submittedBar + 1;
        if (!request.TimeInForce.IsGoodTilCancelled && request.TimeInForce.ExpiryBar < earliestFillBar)
        {
            throw Invalid(submittedBar, nameof(request.TimeInForce),
                FormattableString.Invariant($"GTD ExpiryBar {request.TimeInForce.ExpiryBar} precedes the earliest fill bar {earliestFillBar}"));
        }
    }

    private static ArgumentException Invalid(int submittedBar, string field, string reason)
        => new(string.Create(CultureInfo.InvariantCulture, $"Order request submitted at bar {submittedBar}: {field} {reason}."), field);
}
