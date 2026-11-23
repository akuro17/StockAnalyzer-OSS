# `sa_get_symbol_coverage`

- **Mode**: Read-Only

## Contract & Constraints

Retrieves dataset identity (`dataSetId`, `tickerCount`, `latestBarDate`; `latestBarDate` is `null` when no queried symbol has stored bars), time-series data coverage (stored bar counts, earliest and latest timestamps, and `invalidBarCount` across D1, W1, MN1 via `CandleInvariant.IsValidBar`), and metadata status for the given symbols. Each `timeFrames[]` entry always includes `invalidBarCount` and includes `firstInvalidBarDate` (`yyyy-MM-dd`, oldest violating bar) iff `invalidBarCount >= 1`. Duplicate symbols are returned once. A time frame whose stored bars could not be read is named in `unreadableTimeFrames`, not reported as having no data. At most 100 symbols per call. Read-only.

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
