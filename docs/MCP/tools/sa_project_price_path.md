# `sa_project_price_path`

- **Mode**: Read-Only

## Contract & Constraints

Generates multi-step mathematical price projection trajectories (ARIMA, FFT, SSA, HMM) with lower/upper bands (the projected value plus or minus confidenceMultiplier model standard deviations; a model-based band, not a statistical confidence interval of the price). These numbers are model outputs for research, not advice. Causal as of endDate: a projection is computed only from bars dated on or before endDate, so later bars never change it. Future dates of D1 are consecutive calendar days from asOfDate; for W1 and MN1 they are calendar period starts (Monday of each week, 1st of each month). With timeFrame W1 or MN1 the dates of the projected points are calendar period starts (the Monday of the ISO week, the 1st of the month), so that holidays of future periods do not change them. asOfDate is the label of the last stored bar, which is dated by the start of its period as well, so consecutive dates are exactly 7 days (W1) or one calendar month (MN1) apart. decimalPlaces (0 to 10, optional) rounds the written value, lower and upper of every point (away from zero at the midpoint); the projection and its bands are calculated unrounded.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol ('.' == '-', e.g. 7203-T). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Bar timeframe (D1, W1, MN1). |
| `endDate` | `string` | No | - | Inclusive end date (yyyy-MM-dd). |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate (1..10000). |
| `method` | `string` | Yes | enum: `["Arima","Fft","Ssa","Hmm"]` | Extrapolation method. |
| `horizonBars` | `integer` | No | default: `20`, min: `1`, max: `100` | Forward bars to project (1..100). Default 20. |
| `confidenceMultiplier` | `number` | No | - | Band width in model std devs (0.0..5.0). Default 2.0. |
| `arimaP` | `integer` | No | default: `1`, min: `0`, max: `5` | ARIMA p order (0..5). Default 1. |
| `arimaD` | `integer` | No | default: `1`, min: `0`, max: `2` | ARIMA d differencing (0..2). Default 1. |
| `arimaQ` | `integer` | No | default: `1`, min: `0`, max: `5` | ARIMA q order (0..5). Default 1. |
| `fftHarmonics` | `integer` | No | default: `3`, min: `1`, max: `10` | FFT dominant harmonics (1..10). Default 3. |
| `ssaEmbeddingDimension` | `integer` | No | default: `20`, min: `2`, max: `100` | SSA lag dimension (2..100). Default 20. |
| `ssaNumComponents` | `integer` | No | default: `2`, min: `1`, max: `20` | SSA reconstructed components (1..20). Default 2. |
| `hmmStates` | `integer` | No | default: `2`, min: `2`, max: `3` | HMM state count (2..3). Default 2. |
| `allowUnregistered` | `boolean` | No | - | Allow unregistered symbol with stored bars. Default false. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round value/lower/upper (0..10, output only). Default: unrounded. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "method": "Arima",
  "horizonBars": 20
}
```

## Common Errors

- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
