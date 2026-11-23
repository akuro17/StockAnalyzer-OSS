# `sa_check_portfolio_rl_preflight`

- **Mode**: Read-Only

## Contract & Constraints

Performs preflight checks for portfolio reinforcement learning across 2 to 100 tickers without modifying Data or executing training. Checks date alignment, union calendar length, holdout splits, and per-symbol readiness. Each `symbolDetails[]` entry always includes a path-free `detail` string; when `blocker` is `NoUsableData`, `symbolDetails[]` still reports per-symbol probe entries, and `ReadError` entries caused by invalid stored OHLCV bars are re-scanned at the MCP layer with `CandleInvariant` to surface symbol, date, O/H/L/C/V, and violation diagnostics.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | Yes | - | The list of tickers in the portfolio universe (2 to 100 symbols). |
| `startDate` | `string` | No | - | First date of the period (yyyy-MM-dd). If omitted, proposed automatically. |
| `endDateExclusive` | `string` | No | - | First day NOT in the period (yyyy-MM-dd). If omitted, proposed automatically. |
| `symbolHoldout` | `boolean` | No | - | Whether symbol holdout is requested (default: true). |
| `mode` | `string` | No | enum: `["Standard","ShortHistory"]` | Evaluation mode (Standard, ShortHistory). Default: Standard. |
| `maxAssetWeightPercent` | `integer` | No | default: `100`, min: `1`, max: `100` | Maximum asset weight percent between 1 and 100 (default: 100). |

## Example Call

```json
{
  "symbols": [
    "7203-T",
    "6758-T",
    "9984-T"
  ],
  "mode": "Standard"
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
