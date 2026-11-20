# `sa_get_candles`

- **Mode**: Read-Only

## Contract & Constraints

Returns OHLCV bars of one symbol in chronological order (oldest first), exactly as stored. Supported time frames: D1, W1, MN1; others return UNSUPPORTED_TIMEFRAME. Without dates the latest 'count' bars are returned. With startDate and/or endDate (inclusive, yyyy-MM-dd) the bars are filtered inside the latest 10000 bars of the frame and then the latest 'count' of them are returned. For W1 and MN1 with endDate the daily bars are cut at endDate before the weekly or monthly bars are built, so no day after endDate reaches a returned bar (the window is then the latest 10000 bars up to endDate). Timestamps are bar dates as stored (not UTC-qualified); for W1 and MN1 the timestamp is the start of the period (the Monday of the ISO week, the 1st of the month; a holiday does not move it) and the oldest bar may cover a partial period. The last W1 or MN1 bar is partial when its period (ISO week ending Sunday, calendar month) ends after referenceDate (endDate, or the last stored day); lastBarPartial says so, and completeBarsOnly=true drops such a bar (droppedPartialBar). Prices are in the instrument's price units; volume is in shares. decimalPlaces (0 to 10, optional) rounds open, high, low and close (away from zero at the midpoint); volume is never rounded, and without it the prices are as stored. compactRows true returns candles as an empty array and the same bars as candleColumns (the column names) plus candleRows (one [timestamp, open, high, low, close, volume] row per bar) to save tokens; default false.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars. Supported: D1, W1, MN1. |
| `count` | `integer` | No | default: `200`, min: `1`, max: `10000` | Number of bars in the requested time frame. Default 200, at most 10000. |
| `startDate` | `string` | No | - | First date to include (yyyy-MM-dd). |
| `endDate` | `string` | No | - | Last date to include (yyyy-MM-dd). |
| `completeBarsOnly` | `boolean` | No | - | W1 and MN1 only: drop the last bar when its period has not ended yet (see lastBarPartial). Default false. |
| `decimalPlaces` | `integer` | No | min: `0`, max: `10` | Round open, high, low and close to this many decimal places (0 to 10; away from zero at the midpoint). Volume is never rounded. Default: prices as stored. |
| `compactRows` | `boolean` | No | - | true returns 'candles' as an empty array and the bars as 'candleColumns' plus 'candleRows' (one row per bar, in the order of the columns). Default false. |

## Output Schema (`outputSchema`)

```json
{"type":"object","properties":{"symbol":{"type":"string","description":"Ticker symbol."},"timeFrame":{"type":"string","description":"Time frame of the bars."},"periodUnit":{"type":"string","description":"Unit of period parameters."},"timestampFormat":{"type":"string","description":"Date/time format of bar timestamps."},"units":{"description":"Units of prices and volumes.","type":"object","properties":{"price":{"type":"string","description":"Price unit."},"volume":{"type":"string","description":"Volume unit."}},"required":["price","volume"],"additionalProperties":false},"barCount":{"type":"integer","description":"Number of returned bars."},"requestedCount":{"type":"integer","description":"Requested bar count."},"loadedBarCount":{"type":"integer","description":"Number of bars loaded from storage."},"matchedBarCount":{"type":"integer","description":"Number of bars matched by date filter."},"earliestAvailableTimestamp":{"type":["string","null"],"description":"Earliest available bar date in storage."},"filteredByDate":{"type":"boolean","description":"True when date range filter was applied."},"truncatedByLoadCap":{"type":"boolean","description":"True when load cap truncated history."},"droppedLeadingBar":{"type":"boolean","description":"True when oldest aggregated bar was partial."},"referenceDate":{"type":["string","null"],"description":"Reference date for partial bar calculation."},"lastBarPeriodEnd":{"type":["string","null"],"description":"Period end date for the last bar."},"lastBarPartial":{"type":"boolean","description":"True when the latest bar covers an incomplete period."},"droppedPartialBar":{"type":"boolean","description":"True when incomplete latest bar was dropped."},"candles":{"type":"array","description":"OHLCV bars in chronological order; an empty array when compactRows is true.","items":{"type":"object","properties":{"timestamp":{"type":"string","description":"Bar timestamp."},"open":{"type":"number","description":"Open price."},"high":{"type":"number","description":"High price."},"low":{"type":"number","description":"Low price."},"close":{"type":"number","description":"Close price."},"volume":{"type":"number","description":"Volume in shares."}},"required":["timestamp","open","high","low","close","volume"],"additionalProperties":false}},"candleColumns":{"type":"array","description":"Column names of every candleRows row; present only when compactRows is true.","items":{"type":"string"}},"candleRows":{"type":"array","description":"One [timestamp, open, high, low, close, volume] row per bar, oldest first; present only when compactRows is true.","items":{"type":"array","items":{"type":["string","number"]}}}},"required":["symbol","timeFrame","periodUnit","timestampFormat","units","barCount","requestedCount","loadedBarCount","matchedBarCount","filteredByDate","truncatedByLoadCap","droppedLeadingBar","lastBarPartial","droppedPartialBar","candles"],"additionalProperties":false}
```

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "count": 200
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `UNSUPPORTED_TIMEFRAME`: Requested timeFrame is not supported for stored bars or this tool.
