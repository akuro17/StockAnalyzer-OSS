# `sa_get_rl_playback`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Replays a registered reinforcement learning model over the stored daily bars of a symbol to evaluate action values (Q-values for Hold, Buy, Sell with discount factor 0.99), the selected action, and data continuity/price scale warnings at a given decision position (0 to 127).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `modelId` | `string` | Yes | - | The identifier of a registered RL model generation, or 'active' for the current active RL model. |
| `symbol` | `string` | No | - | Ticker symbol; '.' and '-' are interchangeable. Default: the model's trained symbol. |
| `asOfDate` | `string` | No | - | Anchor date (yyyy-MM-dd) of the observation window. Default: the latest valid anchor date. |
| `decisionPosition` | `integer` | No | default: `0`, min: `0`, max: `127` | Decision step within the episode horizon (0 to 127). Default: 0. |
| `policyId` | `string` | No | - | The evaluation policy ID. Default: the model's target policy. |

## Example Call

```json
{
  "modelId": "active",
  "symbol": "7203-T"
}
```

## Common Errors

- `ARTIFACT_NOT_FOUND`: Requested model or execution artifact was not found.
- `DATA_UNAVAILABLE`: Required workspace, portfolio, notes, model, or drawing file is unavailable.
- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INTERNAL_ERROR`: Unexpected internal tool execution error (see server log).
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
