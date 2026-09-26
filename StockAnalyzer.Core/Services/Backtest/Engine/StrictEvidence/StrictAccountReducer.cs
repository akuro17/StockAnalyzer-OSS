using StockAnalyzer.Core.Models.Backtest.Engine.StrictEvidence;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence;

internal readonly record struct StrictReducerPreview(
    StrictEvidenceVerdict CapitalAvailable,
    StrictEvidenceVerdict MarginSatisfied,
    decimal Commission,
    StrictAccountState StateAfter);

/// <summary>Pure accounting reducer. It never mutates an engine run.</summary>
internal static class StrictAccountReducer
{
    public static StrictReducerPreview Preview(
        StrictAccountState state,
        StrictOrderSnapshot order,
        decimal price,
        StrictBacktestConfiguration configuration)
    {
        checked
        {
            decimal commission = FillPricing.ComputeCommission(
                configuration.FlatCommission,
                configuration.PerUnitCommission,
                order.Quantity);
            if (order.Intent is StrictOrderIntent.ExitPosition or StrictOrderIntent.LiquidatePosition)
            {
                if (state.IsFlat || order.Quantity != AbsPosition(state.PositionQuantity))
                {
                    throw new InvalidOperationException("A reduce-only order must close the complete existing position.");
                }

                decimal cash = state.Cash + state.HeldMargin + (state.PositionQuantity * (price - state.EntryPrice)) - commission;
                return new StrictReducerPreview(
                    StrictEvidenceVerdict.True,
                    StrictEvidenceVerdict.True,
                    commission,
                    new StrictAccountState(cash, 0m, 0L, 0m));
            }

            if (!state.IsFlat)
            {
                throw new InvalidOperationException("An entry order cannot create or reverse a position while another position is open.");
            }

            decimal margin = order.Quantity * price * configuration.InitialMarginRatio;
            decimal requiredCash = margin + commission;
            if (state.Cash < requiredCash)
            {
                return new StrictReducerPreview(
                    StrictEvidenceVerdict.False,
                    StrictEvidenceVerdict.Unknown,
                    commission,
                    state);
            }

            decimal cashAfter = state.Cash - requiredCash;
            long signedQuantity = order.Intent == StrictOrderIntent.EnterLong
                ? order.Quantity
                : checked(-order.Quantity);
            var tentative = new StrictAccountState(cashAfter, margin, signedQuantity, price);
            decimal equityAfter = cashAfter + margin;
            decimal maintenanceRequired = order.Quantity * price * configuration.MaintenanceMarginRatio;
            StrictEvidenceVerdict marginVerdict = equityAfter >= maintenanceRequired
                ? StrictEvidenceVerdict.True
                : StrictEvidenceVerdict.False;
            return new StrictReducerPreview(
                StrictEvidenceVerdict.True,
                marginVerdict,
                commission,
                tentative);
        }
    }

    public static long AbsPosition(long positionQuantity)
        => positionQuantity >= 0L ? positionQuantity : checked(-positionQuantity);

    public static decimal Equity(StrictAccountState state, decimal mark)
    {
        checked
        {
            return state.Cash + state.HeldMargin + (state.PositionQuantity * (mark - state.EntryPrice));
        }
    }

    public static decimal MaintenanceRequirement(
        StrictAccountState state,
        decimal mark,
        StrictBacktestConfiguration configuration)
    {
        checked
        {
            return AbsPosition(state.PositionQuantity) * mark * configuration.MaintenanceMarginRatio;
        }
    }
}
