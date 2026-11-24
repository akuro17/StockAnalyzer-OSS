# `sa_search_tools`

- **Mode**: Meta-Tool (`--progressive-discovery`)

## Contract & Constraints

Searches active StockAnalyzer tools registered for the current server flags (`--enable-execution`, `--enable-discussion`, `--toolset`) by keyword, profile, or exact names without exposing all full schemas upfront. When `--enable-execution` or `--enable-discussion` is active, the `tools/list` description guides callers to both `sa_invoke_tool` (`isReadOnly: true`) and `sa_invoke_action` (`isReadOnly: false`); in read-only mode it references `sa_invoke_tool` only. Split `query` on whitespace (max 10 unique words, 200 chars); omit or pass whitespace to browse sorted by name (`matchedTokens: 0, score: 0`). Matching includes a curated ASCII domain alias table (`McpToolSearchAliases`, tier score `50`), word-boundary and CamelCase/acronym token matching for short tokens (`<= 3` chars), and `-`/`_` separator folding across tool names, profile names, top-level argument names, summaries, and descriptions. Matched results are sorted deterministically by matched unique token count descending, total tier score descending, then tool name ordinal ascending. Each returned entry includes `"docUri": "stockanalyzer://docs/tools/<name>"` for full Markdown documentation lookup via `resources/read`. When `profile != "all"` returns `0` matches, `availableProfiles` lists the active non-meta profiles. Passing unknown names in `toolNames` (max 10 raw entries) populates `unmatchedToolNames` instead of failing, while empty `toolNames[i]` elements return `INVALID_ARGUMENT`. `includeSchema` defaults to `true` only when `toolNames` is provided (`false` otherwise); `limit` caps at `5` for query/profile schema searches (returning `LIMIT_EXCEEDED` with a schema/compact hint when `includeSchema: true` receives `limit > 5` without `toolNames`), `10` when `toolNames` is provided with `includeSchema: true` (default `10`), and `50` when `includeSchema: false` (default `20`).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `query` | `string` | No | max 200 chars, max 10 unique words | Space-separated keywords matched against tool name, profile, top-level arguments, and description. |
| `profile` | `string` | No | enum: `all, core, technical, technical-extended, screener, backtest, portfolio, ml, execution, discussion` | Filter to one toolset or mode profile. Default `all`. |
| `toolNames` | `array` | No | `minItems: 1`, max 10 items | Exact tool names to inspect. Turns `includeSchema` on unless explicitly `false`. |
| `matchMode` | `string` | No | enum: `All, Any` (default `Any`) | `All`: every query word must match. `Any`: at least one word must match. |
| `includeSchema` | `boolean` | No | default `true` with `toolNames`, else `false` | Include `inputSchema` (and `outputSchema` in dual mode); caps `limit` at `5` (`10` when `toolNames` is given). |
| `includeExamples` | `boolean` | No | default `false` | Include `exampleArguments` when available. |
| `offset` | `integer` | No | `minimum: 0`, default `0` | 0-based match offset. |
| `limit` | `integer` | No | `1..5` (`includeSchema: true`), `1..10` (`toolNames` + `includeSchema: true`), `1..50` (`false`) | Maximum results returned per call. |

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
- `LIMIT_EXCEEDED`: `query` exceeds 200 chars or 10 words, `toolNames` exceeds 10 items, `limit` exceeds `5` (`includeSchema: true` without `toolNames`), `10` (`toolNames` + `includeSchema: true`), or `50` (`includeSchema: false`), or response exceeds `MaxResponseTokens` (retry with `limit: 1..2` or `includeSchema: false`).
