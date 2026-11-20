# `sa_get_symbol_coverage`

- **Mode**: Read-Only

## Contract & Constraints

Retrieves time-series data coverage (stored bar counts, earliest and latest timestamps across D1, W1, MN1) and metadata status for the given symbols. Duplicate symbols are returned once. A time frame whose stored bars could not be read is named in unreadableTimeFrames, not reported as having no data. At most 100 symbols per call. Read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | Yes | - | The symbols to inspect. Duplicate symbols are folded. At most 100 symbols per call. |

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

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
