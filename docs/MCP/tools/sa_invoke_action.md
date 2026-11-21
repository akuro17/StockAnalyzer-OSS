# `sa_invoke_action`

- **Mode**: Proxy Meta-Tool (`--progressive-discovery`, exposed only when `--enable-execution` or `--enable-discussion` is active)

## Contract & Constraints

Invokes a state-changing or execution StockAnalyzer tool (`isReadOnly: false`) discovered via `sa_search_tools`. The server validates the outer `{toolName, arguments}` envelope first, rejects empty names or recursive proxy calls targeting `sa_invoke_tool`, `sa_invoke_action`, or `sa_search_tools`, resolves `toolName` strictly against the callable registry (`--enable-execution`, `--enable-discussion`, and `--toolset` gates remain enforced), and rejects read-only targets (`isReadOnly: true`, directing callers to `sa_invoke_tool`). Valid calls execute in a single turn using the target tool's exact concurrency lock, heavy timeout (`HeavyToolTimeoutSeconds`), PNG image response handling (`IMcpImageTool`), `CompactUsageHint`, `OutputSchema`, and `CommandLogObserver` identity. Omitting `arguments` defaults to `{}`; passing `arguments: null` fails with `INVALID_ARGUMENT`. Once `toolName` resolves to a valid non-read-only target, subsequent validation and execution outcomes are recorded under the target tool's name; failures before target resolution are recorded under `sa_invoke_action`.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `toolName` | `string` | Yes | non-empty active non-read-only tool name (`isReadOnly: false`, excluding meta-tools) | Exact state-changing or execution tool name discovered via `sa_search_tools`. |
| `arguments` | `object` | No | JSON object (default `{}`) | Arguments conforming to the target tool's `inputSchema`. |

## Example Call

```json
{
  "toolName": "sa_discussion_create",
  "arguments": {
    "title": "Market check"
  }
}
```

## Common Errors

- `INVALID_ARGUMENT`: Empty, disabled, unknown, read-only (`isReadOnly: true`), or meta-tool `toolName`, non-object `arguments`, or inner schema validation failure (`arguments.<param>` with hint pointing to `sa_search_tools`).
- Target tool domain errors (`LIMIT_EXCEEDED`, `EXECUTION_TIMEOUT`, `SERVER_BUSY`, `JOB_NOT_FOUND`, `DISCUSSION_NOT_FOUND`, etc.): returned verbatim from the target tool (with `param` prefixed by `arguments.` when non-null).
