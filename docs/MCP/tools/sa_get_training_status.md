# `sa_get_training_status`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Returns the state of a job started with sa_start_training, sa_start_rl_training, or sa_start_portfolio_rl_training (kind says which): Running, Cancelling, Validating, Succeeded, Failed or Cancelled (the last three are final). stage is the trainer's current step and progressPercent is 0 to 100 (0 until the first progress report). metrics, when present, hold the latest numbers the trainer reported (null where a number is not finite). messages are the latest status lines; request echoes the settings actually used. When Succeeded, artifact describes the model (artifactId, file name, size, sha256 and whether it loads with its input and output shapes, -1 for a dynamic dimension) and sa_run_onnx_prediction can use it; when Failed, failureReason is TIMEOUT, INVALID_ONNX or TRAINING_FAILED. The model lives in a temporary folder and is removed when the server stops; to keep a model, repeat the run in the Training Wizard with the echoed request and register it there. A reinforcement-learning job (kind reinforcement_learning) echoes its period in request, has no metrics or artifact, and when Succeeded reports result: validationMeanReturn (exact decimal text), candidateSeed, checkpointStep, trainingTransitions, model size and sha256, and the source revision; its files are removed when it ends. A portfolio reinforcement-learning job (kind portfolio_reinforcement_learning) echoes its parameters in request, and when Succeeded reports policyReturn and baselineComparison (cashReturn, equalBuyHoldReturn, equalDailyRebalancedReturn), selectedSeed, selectedCheckpoint, hashes and model identity.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `jobId` | `string` | Yes | - | The jobId returned by sa_start_training, sa_start_rl_training, or sa_start_portfolio_rl_training. |

## Example Call

```json
{
  "jobId": "00000000-0000-0000-0000-000000000000"
}
```

## Common Errors

- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
