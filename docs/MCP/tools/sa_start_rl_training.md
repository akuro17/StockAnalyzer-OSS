# `sa_start_rl_training`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Starts one reinforcement-learning smoke run (the trial run of the RL window: one seed, 1024 transitions, the Backtest window's factory costs) on the stored daily bars of one symbol and returns at once with a jobId; poll sa_get_training_status until the state is final (sa_cancel_training stops it). It needs a stored equity with its metadata and the application's managed training Python environment (PYTHON_ENV_MISSING otherwise). Nothing is saved under Data and no model is registered: a succeeded job reports its validation mean return, seed, checkpoint, model size and sha256, and its files are removed at once. Omitted dates are proposed from the stored data as the RL window does (end = the day after the latest final bar). The jobs share one limit with the supervised ones (EXECUTION_BUSY). A server whose temporary folder path is too long for the trainer is refused before anything is created (SANDBOX_PATH_TOO_LONG).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | The symbol to train on ('.' and '-' are interchangeable). |
| `startDate` | `string` | No | - | First date of the period (yyyy-MM-dd). Default: the earliest start from which the required bars are stored. |
| `endDateExclusive` | `string` | No | - | First day NOT in the period (yyyy-MM-dd); the last stored bar is the day before. Default: the day after the latest final stored bar. |

## Example Call

```json
{
  "symbol": "7203-T"
}
```

## Common Errors

- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `UNKNOWN_INDICATOR`: Unknown indicator type name; check sa_get_capabilities.
