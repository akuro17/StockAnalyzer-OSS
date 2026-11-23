# `sa_get_settings`

- **Mode**: Read-Only

## Contract & Constraints

Returns dataset identity (`dataSetId` matching `^[0-9a-f]{8}$`, registered `tickerCount`, and `latestBarDate`, which is `null` when `tickerCount == 0`; two distinct path spellings to the same directory may produce different `dataSetId` hashes) alongside selected StockAnalyzer settings that shape what the tools accept: the default symbol, screener and backtest condition limits, the zigzag threshold (percent) and the prediction window. File locations, credentials and interface settings are never returned. Without section, all sections are returned.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `section` | `string` | No | enum: `["chart","screener","backtest","marketStructure","prediction"]` | Only this section. |

## Example Call

```json
{
  "section": "Screener"
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
