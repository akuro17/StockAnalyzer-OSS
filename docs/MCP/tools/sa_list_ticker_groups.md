# `sa_list_ticker_groups`

- **Mode**: Read-Only

## Contract & Constraints

Lists ticker groups (All Tickers, watchlists, portfolios, custom categories, list folders, and Tickers-tab filters) from the saved workspace.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `includeRules` | `boolean` | No | - | When true, each filter node includes its configured filter rules. Default false. |

## Example Call

```json
{
  "includeRules": true
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
