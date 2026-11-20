# `sa_calculate_cross_asset_stats`

- **Mode**: Read-Only

## Contract & Constraints

Calculates cross-asset return statistics across 2 to 50 symbols: correlation matrix, sample covariance matrix (N-1 degrees of freedom), and benchmark Beta, Alpha, and R2. Aligns bars using an inner join on common trading dates. With timeFrame W1 or MN1 the bars are matched by period. With timeFrame W1 or MN1 the dates of this tool are calendar period starts (the Monday of the ISO week, the 1st of the month), the same label a W1 or MN1 bar has in every tool, so that holidays of different markets do not change them. The first and last trading day of a period are not part of the date. multiBarReturnCount counts the common dates whose returns cover different holding periods because a market traded on a day another was closed. holdingPeriodMode IncludeAll (default) uses those dates unchanged; StrictEqualHoldingPeriod leaves them out and calculates on the rest, and then adds holdingPeriodMode and excludedMultiBarDateCount to the result (commonDateCount is what remains; with fewer than 3 left the call fails with INSUFFICIENT_MARKET_DATA). Supports returnBasis 'SimpleReturn' (default) or 'LogReturn'. includeDistributionStats true (default false) adds downsideBeta and upsideBeta to every betaAlpha entry (the OLS beta inside the dates with a strictly negative / strictly positive benchmark return; a zero benchmark return belongs to neither; null below 3 such dates or without benchmark variance there) and a symbolStats array in symbols order: mean, stdDev (sample, N-1), downsideDeviation (target 0), skewness (G1, 3+ returns), excessKurtosis (G2, 4+), jarqueBeraStat and jarqueBeraPValue (8+; p = exp(-JB/2)), cornishFisherVar95 (4+; the 5 % lower-tail return quantile with the Cornish-Fisher correction: a LOSS IS NEGATIVE, so it equals minus the positive valueAtRisk convention of sa_analyze_portfolio_risk; null where the expansion is not monotone). decimalPlaces (0 to 10, optional) rounds every written statistic (away from zero at the midpoint; output only, the statistics are calculated unrounded); counts and dates are never rounded. Statistics that are undefined for the data (a flat series, too few returns) are null. Read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | No | - | Universe ticker symbols to analyze (2 to 50). Mutually exclusive with groupId and symbolsFromResultId. |
| `groupId` | `string` | No | - | Guid of a saved ticker group (from sa_list_ticker_groups) to analyze. Mutually exclusive with symbols and symbolsFromResultId. |
| `symbolsFromResultId` | `string` | No | - | Handle id from sa_run_screener or sa_filter_tickers (storeResult true). Mutually exclusive with symbols and groupId. |
| `resultOffset` | `integer` | No | default: `0`, min: `0` | 0-based start index within symbolsFromResultId. Default 0. |
| `resultLimit` | `integer` | No | min: `2`, max: `50` | Maximum symbols to read from symbolsFromResultId (2..50). |
| `benchmark` | `string` | No | - | Optional benchmark symbol to compute Beta, Alpha, and R2 against. |
| `timeFrame` | `string` | No | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars. Supported: D1, W1, MN1. Default D1. |
| `lookbackBars` | `integer` | No | default: `200`, min: `1`, max: `10000` | Stored bars each symbol evaluates, newest last. Default 200, maximum 10000. |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `returnBasis` | `string` | No | enum: `["SimpleReturn","LogReturn"]` | Return calculation basis: 'SimpleReturn' (default) or 'LogReturn'. |
| `holdingPeriodMode` | `string` | No | enum: `["IncludeAll","StrictEqualHoldingPeriod"]` | Common return dates whose returns cover different holding periods (multiBarReturnCount): 'IncludeAll' (default) uses them unchanged, 'StrictEqualHoldingPeriod' leaves them out. |
| `includeCovariance` | `boolean` | No | - | Whether to include the sample covariance matrix in the result. Default false. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round every correlation, covariance, beta, alpha, R2 and distribution statistic (0..10, output only). Default: unrounded. |
| `includeDistributionStats` | `boolean` | No | - | Whether to add downsideBeta / upsideBeta to betaAlpha (needs a benchmark) and the symbolStats array (mean, stdDev, downsideDeviation, skewness, excessKurtosis, jarqueBeraStat, jarqueBeraPValue, cornishFisherVar95; the VaR is the negative lower-tail return). Default false: the output is unchanged. |

## Example Call

```json
{
  "symbols": [
    "MSFT",
    "7203-T"
  ],
  "timeFrame": "D1",
  "benchmark": "MSFT",
  "returnBasis": "SimpleReturn"
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
