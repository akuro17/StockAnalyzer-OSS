# `sa_detect_chart_patterns`

- **Mode**: Read-Only

## Contract & Constraints

Detects macro chart patterns (Geometric formations, Harmonic patterns, Elliott waves, and DTW Waveform matching) on historical candles, strictly filtered by confirmation date: no pattern confirmed after endDate is returned and bars after endDate are never read. Not prefix-invariant: the set for an earlier endDate is not guaranteed to be the earlier part of the set for a later endDate (Geometric overlap filter and longer-fit replacement, one WaveformMatching occurrence per template).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol ('.' == '-', e.g. 7203-T). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). |
| `endDate` | `string` | No | - | Inclusive end date (yyyy-MM-dd). |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate (1..10000). |
| `families` | `array` | No | - | Families to detect: Geometric, Harmonic, ElliottWave, WaveformMatching. Default: all. |
| `maxPatterns` | `integer` | No | default: `100`, min: `1`, max: `100` | Max patterns returned (1..100). Default 100. |
| `geometricZigZagThreshold` | `number` | No | - | Geometric pivot % threshold (0.1..50.0). Default 2.0. |
| `harmonicZigZagThreshold` | `number` | No | - | Harmonic min leg swing % (0.1..50.0). Default 2.0. |
| `elliottZigZagThreshold` | `number` | No | - | Elliott pivot % threshold (0.1..50.0). Default 5.0. |
| `dtwMinWindow` | `integer` | No | default: `20`, min: `2`, max: `10000` | DTW min window bars (2..10000). Default 20. |
| `dtwMaxWindow` | `integer` | No | default: `60`, min: `2`, max: `10000` | DTW max window bars (minWindow..10000). Default 60. |
| `dtwWindowStep` | `integer` | No | default: `5`, min: `1`, max: `10000` | DTW sliding step bars (1..10000). Default 5. |
| `dtwThreshold` | `number` | No | - | DTW min similarity (0.0..1.0). Default 0.5. |
| `allowUnregistered` | `boolean` | No | - | Allow unregistered symbol with stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "families": [
    "Harmonic",
    "Geometric"
  ],
  "maxPatterns": 50
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
