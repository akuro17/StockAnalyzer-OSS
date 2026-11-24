# `sa_list_result_handles`

- **Mode**: Read-Only

## Contract & Constraints

Lists active in-memory symbol result handles (max 16) created by `sa_run_screener` or `sa_filter_tickers` (`storeResult: true`), ordered most-recently-used (MRU) first without mutating LRU order or extending expiration. Returns `maxHandles`, `activeCount`, and `handles[{resultId, sourceTool, symbolCount, previewSymbols}]`. Universe-filter tools (`sa_run_screener`, `sa_filter_tickers`) accept an empty (0-symbol) handle at `resultOffset: 0` and return 0 matches cleanly, whereas detail/analysis tools (such as `sa_calculate_indicators_batch`) require at least 1 symbol (`INVALID_ARGUMENT` when empty). Never mutates `Data` or fetches remote data.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `previewCount` | `integer` | No | default: `5`, min: `0`, max: `20` | Leading symbols to preview per handle (0..20). Default 5; pass `0` to omit `previewSymbols`. |

## Example Call

```json
{
  "previewCount": 5
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, or `previewCount` outside `0..20`.
- `LIMIT_EXCEEDED`: Requested count or estimated response tokens exceeded server bounds.
