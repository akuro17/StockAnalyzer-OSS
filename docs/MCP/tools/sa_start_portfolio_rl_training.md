# `sa_start_portfolio_rl_training`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Starts one sandboxed reinforcement-learning portfolio training run across 2 to 100 tickers in the temporary sandbox and returns at once with a jobId; poll sa_get_training_status until the state is final (sa_cancel_training stops it). Executes discrete allocation smoke training (1 seed, 1024 transitions, factory costs). No files are written to Data/; artifacts are held in the ephemeral sandbox and cleared upon completion. Returns jobId, state, and baseline comparisons (Cash, Equal-Weight Buy & Hold, Equal-Weight Daily Rebalanced returns) once succeeded.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | Yes | - | The list of tickers in the portfolio universe (2 to 100 symbols). |
| `startDate` | `string` | No | - | First date of the period (yyyy-MM-dd). Default: proposed from stored data. |
| `endDateExclusive` | `string` | No | - | First day NOT in the period (yyyy-MM-dd). Default: proposed from stored data. |
| `symbolHoldout` | `boolean` | No | - | Whether symbol holdout is requested (default: true). |
| `maxAssetWeightPercent` | `integer` | No | default: `100`, min: `1`, max: `100` | Maximum asset weight percent between 1 and 100 (default: 100). |
| `mode` | `string` | No | enum: `["Standard","ShortHistory"]` | Evaluation mode (Standard, ShortHistory). Default: Standard. |

## Example Call

```json
{
  "symbols": [
    "7203-T",
    "6758-T"
  ]
}
```

## Common Errors

- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
