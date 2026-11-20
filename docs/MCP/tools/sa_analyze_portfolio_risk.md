# `sa_analyze_portfolio_risk`

- **Mode**: Read-Only

## Contract & Constraints

Historical risk of the user's saved portfolio over the last lookbackBars daily bars: value at risk and conditional value at risk of the portfolio's one-bar return at confidenceLevel (as positive loss ratios), portfolio volatility, and per holding its weight, marginal and component volatility and tail component of the conditional value at risk. Weights are signed market values in the base currency (baseCurrency, ISO 4217, default JPY) divided by the gross exposure (the sum of the absolute values); shorts are negative; a symbol held long and short is netted. Market values and exchange rates are those of sa_get_portfolio_summary; a holding without a stored price or without a rate to the base currency is listed in excludedSymbols and left out of the risk figures (its value stays in the gross exposure). All figures are per daily bar, not annualized, from simple returns on the bars every held symbol shares; volatility uses the sample covariance (N-1). The definitions are echoed in 'definitions'. Optional benchmark (a symbol) adds the portfolio beta (the weighted sum of the holdings' betas). Historical figures are not forecasts. decimalPlaces (0 to 10, optional) rounds the written exposures, risk figures, weights and volatilities (away from zero at the midpoint); the figures are calculated unrounded, and confidenceLevel is the value you sent, as sent. Read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `baseCurrency` | `string` | No | - | ISO 4217 code of the currency the exposures are measured in. Default JPY. |
| `lookbackBars` | `integer` | No | default: `200`, min: `1`, max: `10000` | Daily bars the returns are taken from, newest last. Default 200, maximum 10000. |
| `confidenceLevel` | `number` | No | - | Confidence level of the value at risk, from 0.5 to 0.999. Default 0.95. |
| `benchmark` | `string` | No | - | Optional benchmark symbol; adds the portfolio beta. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round every exposure, risk figure, weight and volatility (confidenceLevel is echoed as sent) (0..10, output only). Default: unrounded. |

## Example Call

```json
{
  "baseCurrency": "JPY",
  "lookbackBars": 250,
  "confidenceLevel": 0.95
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
