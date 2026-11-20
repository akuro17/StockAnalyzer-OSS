# `sa_list_tickers`

- **Mode**: Read-Only

## Contract & Constraints

Lists the registered ticker symbols (All Tickers), sorted by symbol (ordinal, ascending). Symbols are canonical: upper case with '.' replaced by '-' (for example 7203-T). searchQuery matches symbol or stored short name, ignoring case. includeMetadataSummary adds the stored identity fields (short name, long name, region, sector, industry, currency) to each returned ticker. fields (optional, requires includeMetadataSummary true) limits the added summary fields to the named camelCase properties to save tokens (at least one, an unknown name is INVALID_ARGUMENT; symbol, hasMetadata are always written). groupId restricts the symbols to the members of a saved ticker group (from sa_list_ticker_groups).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `offset` | `integer` | No | default: `0`, min: `0` | Number of matching tickers to skip. Default 0. |
| `limit` | `integer` | No | default: `100`, min: `1`, max: `1000` | Page size. Default 100, at most 1000. |
| `searchQuery` | `string` | No | - | Case-insensitive text that must occur in the symbol or the stored short name. |
| `includeMetadataSummary` | `boolean` | No | - | Add stored identity fields to each returned ticker. At most 100 tickers per call. |
| `fields` | `array` | No | - | Summary properties to include when includeMetadataSummary is true (camelCase names, for example shortName or sector); symbol, hasMetadata are always written. Default: all summary properties. |
| `groupId` | `string` | No | - | Guid of a ticker group from sa_list_ticker_groups. When given, lists only members of that group. |

## Output Schema (`outputSchema`)

```json
{"type":"object","properties":{"groupId":{"type":"string","description":"Identifier of the ticker group."},"groupKind":{"type":"string","description":"Kind of the ticker group."},"groupUnregisteredMemberCount":{"type":"integer","description":"Member count of unregistered tickers in the group."},"groupUnknownRuleFields":{"type":"array","description":"Unknown rule fields found in dynamic rule definitions.","items":{"type":"string"}},"totalCount":{"type":"integer","description":"Total number of tickers matching the criteria."},"offset":{"type":"integer","description":"Paging offset applied."},"limit":{"type":"integer","description":"Page size limit applied."},"returnedCount":{"type":"integer","description":"Number of tickers returned in this page."},"hasMore":{"type":"boolean","description":"True when more tickers remain after this page."},"tickers":{"type":"array","description":"The matching tickers for this page.","items":{"type":"object","properties":{"symbol":{"type":"string","description":"Ticker symbol (canonical form)."},"hasMetadata":{"type":"boolean","description":"True when stored metadata exists."},"shortName":{"type":"string","description":"Stored ShortName."},"longName":{"type":"string","description":"Stored LongName."},"region":{"type":"string","description":"Stored Region."},"sector":{"type":"string","description":"Stored Sector."},"industry":{"type":"string","description":"Stored Industry."},"currency":{"type":"string","description":"Stored Currency."}},"required":["symbol"],"additionalProperties":false}}},"required":["totalCount","offset","limit","returnedCount","hasMore","tickers"],"additionalProperties":false}
```

## Example Call

```json
{
  "limit": 20,
  "searchQuery": "TOYOTA"
}
```

## Common Errors

- `DATA_UNAVAILABLE`: Required workspace, portfolio, notes, model, or drawing file is unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
