# `sa_get_portfolio_summary`

- **Mode**: Read-Only

## Contract & Constraints

Returns the user's saved portfolio valued by the application's own valuation: total value, unrealized and realized profit, cash and its ratio, every open position with its latest stored price, market value and unrealized profit in the base currency (baseCurrency, an ISO 4217 code, default JPY), the cash by currency and the exchange rates used. The exchange rates are the ones the user's own transactions applied (the newest per currency and their inverses); nothing is fetched. A currency without a rate to the base currency is listed in unconvertedCurrencies and its positions are left out of the base-currency totals (a portfolio that holds a single currency is valued in it unchanged). A position without a stored price is valued at its average cost with no unrealized profit (priceAvailable false). Short positions have a negative market value. Read-only; the file is read as it is. No portfolio saved: portfolioFound false and empty lists. includeClosedPositions true adds closedPositions and includeTransactions true adds transactions, each {totalCount, returnedCount, truncated, items}: closed positions newest exit first (id, ticker, side Long or Short, quantity, entryPrice, exitPrice, entryTime, exitTime, realizedPnL, totalFees; in the currency of the trade, which a closed position does not record), transactions newest first (id, executedAt, type Long, ExitLong, Deposit, Withdrawal, Short or ExitShort, ticker, quantity, pricePerUnit, priceCurrency, cashAmount, fee, commissionCurrency, appliedRate when one was applied; the notes, target price and stop loss of a transaction are never returned). Each list returns at most limit items (default 200 for closed positions, 200 for transactions). The totals above, closedPositionCount and realizedPLByCurrency are always for the whole portfolio. There is no allocation breakdown.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `baseCurrency` | `string` | No | - | ISO 4217 code of the currency every total is shown in. Default JPY. |
| `includeClosedPositions` | `boolean` | No | - | true adds closedPositions: the closed trades, newest exit first. Default false. |
| `includeTransactions` | `boolean` | No | - | true adds transactions: the saved transaction history, newest first (notes, target price and stop loss are never returned). Default false. |
| `limit` | `integer` | No | min: `1`, max: `200` | Most items per requested list (needs includeClosedPositions or includeTransactions). Default and maximum 200 closed positions and 200 transactions. |

## Example Call

```json
{
  "baseCurrency": "JPY"
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
