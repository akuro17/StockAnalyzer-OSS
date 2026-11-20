# `sa_find_support_resistance`

- **Mode**: Read-Only

## Contract & Constraints

Extracts support and resistance price levels, hit counts, and strength scores using Singular Spectrum Analysis (SSA). Computes active nearest support/resistance levels and distance ratios relative to the current price. Read-only. Causal as of endDate (bars after it are never read), but the SSA reconstruction smooths over the whole loaded window including its last bar, so the levels are not prefix-invariant: a level can move when later bars are added. clusterTolerance is a multiplier of residualStdDev; effectiveClusterTolerance is the resulting price distance.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars (D1, W1, MN1). |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). If specified, bars up to this date are evaluated. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate, newest last. Default and maximum 10000. |
| `mode` | `string` | No | enum: `["StructuralPivots","DynamicEnvelopes","ProjectedTargets"]` | SSA calculation mode: StructuralPivots, DynamicEnvelopes, ProjectedTargets. Default StructuralPivots. |
| `embeddingDimension` | `integer` | No | default: `15`, min: `2` | SSA embedding dimension / lag window length (>= 2). Default 15. |
| `numComponents` | `integer` | No | default: `2`, min: `1` | Number of reconstructed SSA components when autoRank is false (>= 1). Default 2. |
| `autoRank` | `boolean` | No | - | Automatically estimate signal rank via cumulative energy threshold. Default true. |
| `maxLevels` | `integer` | No | default: `20`, min: `1`, max: `20` | Maximum total support and resistance levels to return (1..20). Default 20. |
| `clusterTolerance` | `number` | No | - | StructuralPivots: multiplier of residualStdDev merging close extrema into effectiveClusterTolerance. Default 0.5. |
| `multiplier` | `number` | No | - | Volatility multiplier for DynamicEnvelopes mode. Default 2.0. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "mode": "StructuralPivots",
  "maxLevels": 10
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
