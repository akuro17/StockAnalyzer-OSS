# `sa_invoke_tool`

- **Mode**: Proxy Meta-Tool (`--progressive-discovery`)

## Contract & Constraints

Invokes an active StockAnalyzer tool discovered via `sa_search_tools`. The server validates the outer `{toolName, arguments}` envelope first, rejects empty names or recursive proxy calls targeting `sa_invoke_tool` or `sa_search_tools`, resolves `toolName` strictly against the callable registry (`--enable-execution`, `--enable-discussion`, and `--toolset` gates remain enforced), and executes the target in a single turn using the target tool's exact concurrency lock, heavy timeout (`HeavyToolTimeoutSeconds`), PNG image response handling (`IMcpImageTool`), `CompactUsageHint`, `OutputSchema`, and `CommandLogObserver` identity. Omitting `arguments` defaults to `{}`; passing `arguments: null` fails with `INVALID_ARGUMENT`. Once `toolName` resolves to a valid active target, subsequent validation and execution outcomes are recorded under the target tool's name; failures before target resolution are recorded under `sa_invoke_tool`.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `toolName` | `string` | Yes | non-empty active tool name (excluding `sa_search_tools` / `sa_invoke_tool`) | Exact tool name discovered via `sa_search_tools`. |
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

- `INVALID_ARGUMENT`: Empty, disabled, unknown, or meta-tool `toolName`, non-object `arguments`, or inner schema validation failure (`arguments.<param>` with hint pointing to `sa_search_tools`).
- Target tool domain errors (`SYMBOL_NOT_FOUND`, `LIMIT_EXCEEDED`, `EXECUTION_TIMEOUT`, `SERVER_BUSY`, etc.): returned verbatim from the target tool.
