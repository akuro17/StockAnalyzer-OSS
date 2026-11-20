# `sa_list_training_jobs`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Lists training jobs tracked in the current server session, sorted newest first. Returns job summaries (jobId, kind, state, stage, elapsedSeconds) without exposing model internals or requests; details are retrieved via sa_get_training_status.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `limit` | `integer` | No | default: `64`, min: `1`, max: `64` | Optional maximum number of jobs to return (1 to 64). Default 64. |

## Example Call

```json
{}
```

## Common Errors

- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
