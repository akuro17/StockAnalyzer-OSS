# `sa_cancel_training`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Asks a training job (supervised or reinforcement learning) to stop and returns its state: Cancelling while the trainer is being stopped, Cancelled once it is. A job that already ended keeps its final state. A cancelled job leaves no model behind.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `jobId` | `string` | Yes | - | The jobId returned by sa_start_training. |

## Example Call

```json
{
  "jobId": "00000000-0000-0000-0000-000000000000"
}
```

## Common Errors

- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
