# `sa_list_upcoming_catalysts`

- **Mode**: Read-Only

## Contract & Constraints

Lists the user's Ticker Notes that carry a catalyst date (an expected event such as earnings), from fromDate to fromDate + horizonDays inclusive, earliest first. fromDate (yyyy-MM-dd) defaults to today's local date and the resolved value is always returned; horizonDays defaults to and is at most 365. ticker narrows to one symbol. At most 100 are returned (limit); totalMatched counts all and truncated says when more exist. daysUntil counts calendar days from fromDate. Notes in the trash are never listed. A catalyst date is the user's own note, not a verified event date. Read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `fromDate` | `string` | No | - | First date of the window (yyyy-MM-dd). Default: today's local date. |
| `horizonDays` | `integer` | No | default: `365`, min: `1`, max: `365` | Days after fromDate the window reaches. Default and maximum 365. |
| `ticker` | `string` | No | - | Only notes whose related ticker is this symbol. |
| `limit` | `integer` | No | default: `100`, min: `1`, max: `100` | Most notes returned. Default and maximum 100. |

## Example Call

```json
{
  "horizonDays": 30
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
