# `sa_check_rl_playback_preflight`

- **Mode**: Read-Only

## Contract & Constraints

Checks whether a registered reinforcement learning model can be replayed on a symbol's stored daily bars, and reports the valid anchor date range ([earliestAnchorDate, latestAnchorDate]), episode horizon (128 bars), observation window (64 bars), and model metadata. When `canReplay` is `false` due to invalid stored OHLCV bars, the MCP layer enriches `reason` via `CandleInvariant` first-bad-bar diagnostics while scrubbing absolute paths. Never writes the Data tree or the model registry; it may create and delete a transient working folder under the operating system's temporary folder while it reads the stored bars.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `modelId` | `string` | Yes | - | The identifier of a registered RL model generation, or 'active' for the current active RL model. |
| `symbol` | `string` | No | - | Ticker symbol; '.' and '-' are interchangeable. Default: the model's trained symbol. |

## Example Call

```json
{
  "modelId": "active",
  "symbol": "7203-T"
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
