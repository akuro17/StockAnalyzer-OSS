# `sa_search_tools`

- **Mode**: Meta-Tool (`--progressive-discovery`)

## Contract & Constraints

Searches active StockAnalyzer tools registered for the current server flags (`--enable-execution`, `--enable-discussion`, `--toolset`) by keyword, profile, or exact names without exposing all full schemas upfront. Split `query` on whitespace (max 10 unique words, 200 chars); omit or pass whitespace to browse sorted by name (`score: 0`). Separators `-` and `_` are treated as equivalent in both directions during token comparison across tool names, profile names, top-level argument names, summaries, and descriptions. When `profile != "all"` returns `0` matches, `availableProfiles` lists the active non-meta profiles. Passing unknown names in `toolNames` (max 10 raw entries) populates `unmatchedToolNames` instead of failing, while empty `toolNames[i]` elements return `INVALID_ARGUMENT`. `includeSchema` defaults to `true` only when `toolNames` is provided (`false` otherwise) and caps `limit` at `5` (`50` when `includeSchema` is `false`).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `query` | `string` | No | max 200 chars, max 10 unique words | Space-separated keywords matched against tool name, profile, top-level arguments, and description. |
| `profile` | `string` | No | enum: `all, core, technical, technical-extended, screener, backtest, portfolio, ml, execution, discussion` | Filter to one toolset or mode profile. Default `all`. |
| `toolNames` | `array` | No | `minItems: 1`, max 10 items | Exact tool names to inspect. Turns `includeSchema` on unless explicitly `false`. |
| `matchMode` | `string` | No | enum: `All, Any` (default `All`) | `All`: every query word must match. `Any`: at least one word must match. |
| `includeSchema` | `boolean` | No | default `true` with `toolNames`, else `false` | Include `inputSchema` (and `outputSchema` in dual mode); caps `limit` at `5`. |
| `includeExamples` | `boolean` | No | default `false` | Include `exampleArguments` when available. |
| `offset` | `integer` | No | `minimum: 0`, default `0` | 0-based match offset. |
| `limit` | `integer` | No | `1..5` (`includeSchema: true`), `1..50` (`false`) | Maximum results returned per call. |

## Example Call

```json
{
  "query": "walk-forward backtest",
  "includeSchema": true,
  "limit": 3
}
```

## Common Errors

- `INVALID_ARGUMENT`: Wrong type, unsupported enum value, negative offset/limit, or empty string element inside `toolNames`.
- `LIMIT_EXCEEDED`: `query` exceeds 200 chars or 10 words, `toolNames` exceeds 10 items, `limit` exceeds 5 (`includeSchema: true`) or 50 (`false`), or response exceeds `MaxResponseTokens` (retry with `limit: 1..2` or `includeSchema: false`).
