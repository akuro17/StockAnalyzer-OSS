# `sa_get_ticker_metadata`

- **Mode**: Read-Only

## Contract & Constraints

Returns the metadata stored locally for each symbol (company names, region, sector, industry, currency, prices, fundamentals) and the date of the newest stored bar. Nothing is fetched from the internet: found is false when nothing is stored. Fields without a stored value are omitted. The record also contains the user's own entries (tag, notes, reminder and strategy prices such as long, exitLong and stopLossLong) when the user has made them; they are the user's personal annotations. timeSeriesLastUpdated is the date of the newest stored daily bar, or the date the data file was last written when the bar dates cannot be read. Duplicate symbols (after canonicalisation) are returned once. At most 100 symbols per call. fields (optional) limits the metadata object to the named properties to save tokens: camelCase names as they appear in the result, at least one, an unknown name is INVALID_ARGUMENT; symbol, found, timeSeriesLastUpdated are always written (naming them changes nothing), and the order is always the fixed one, not the order of fields. Default: every stored property.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | No | - | Ticker symbols (1 to 100); '.' and '-' are interchangeable. Mutually exclusive with symbolsFromResultId; one of the two is required. |
| `symbolsFromResultId` | `string` | No | - | Handle id from sa_run_screener or sa_filter_tickers (storeResult true). Mutually exclusive with symbols. |
| `resultOffset` | `integer` | No | default: `0`, min: `0` | 0-based start index within symbolsFromResultId. Default 0. |
| `resultLimit` | `integer` | No | min: `1`, max: `100` | Maximum symbols to read from symbolsFromResultId (1..100). |
| `fields` | `array` | No | - | Metadata properties to return (camelCase names, for example sector or marketCap); symbol, found, timeSeriesLastUpdated are always written. Default: every stored property. |

## Example Call

```json
{
  "symbols": [
    "7203-T",
    "6758-T"
  ]
}
```

## Common Errors

- `DATA_UNAVAILABLE`: Required workspace, portfolio, notes, model, or drawing file is unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
