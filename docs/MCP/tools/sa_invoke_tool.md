# `sa_invoke_tool`

- **Mode**: Proxy Meta-Tool (`--progressive-discovery`)

## Contract & Constraints

Invokes a read-only active StockAnalyzer tool (`isReadOnly: true`) discovered via `sa_search_tools`. The server validates the outer `{toolName, arguments}` envelope first, rejects empty names or recursive proxy calls targeting `sa_invoke_tool`, `sa_invoke_action`, or `sa_search_tools`, resolves `toolName` strictly against the callable registry (`--enable-execution`, `--enable-discussion`, and `--toolset` gates remain enforced), and rejects non-read-only targets (`isReadOnly: false`, directing callers to `sa_invoke_action`). Valid calls execute in a single turn using the target tool's exact concurrency lock, heavy timeout (`HeavyToolTimeoutSeconds`), PNG image response handling (`IMcpImageTool`), `CompactUsageHint`, `OutputSchema`, and `CommandLogObserver` identity. Omitting `arguments` defaults to `{}`; passing `arguments: null` fails with `INVALID_ARGUMENT`. Once `toolName` resolves to a valid active read-only target, subsequent validation and execution outcomes are recorded under the target tool's name; failures before target resolution are recorded under `sa_invoke_tool`.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `toolName` | `string` | Yes | non-empty active read-only tool name (`isReadOnly: true`, excluding meta-tools) | Exact read-only tool name discovered via `sa_search_tools`. |
| `arguments` | `object` | No | JSON object (default `{}`) | Arguments conforming to the target tool's `inputSchema`. |

## Example Call

```json
{
  "toolName": "sa_get_capabilities",
  "arguments": {
    "category": "Oscillator"
  }
}
```

## Common Errors

- `INVALID_ARGUMENT`: Empty, disabled, unknown, non-read-only (`isReadOnly: false`), or meta-tool `toolName`, non-object `arguments`, or inner schema validation failure (`arguments.<param>` with hint pointing to `sa_search_tools`).
- Target tool domain errors (`SYMBOL_NOT_FOUND`, `LIMIT_EXCEEDED`, `EXECUTION_TIMEOUT`, `SERVER_BUSY`, etc.): returned verbatim from the target tool (with `param` prefixed by `arguments.` when non-null).
