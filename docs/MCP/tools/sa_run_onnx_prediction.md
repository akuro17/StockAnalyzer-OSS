# `sa_run_onnx_prediction`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Runs a model trained with sa_start_training or a registered production model (modelId) over the stored daily bars of one symbol. Exactly one of 'artifactId' or 'modelId' must be specified. Each prediction uses only the model's window of bars that ends at its own bar (never a later bar): the latest 'count' bars up to asOfDate are predicted, oldest first. A classifier returns classScores (probabilities, the model's own class order), predictedClass (the most probable class) and confidence (its probability); a regressor returns value (the predicted log return). modelContract lists the window, feature mode, classes and the sha256 of the model. These numbers are model outputs for research, not advice. The model must be a succeeded job's (see sa_get_training_status) or 'latest', or a registered modelId (or 'active').

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `artifactId` | `string` | No | - | The artifactId of a succeeded job, or 'latest' for the newest model. |
| `modelId` | `string` | No | - | The identifier of a registered model generation, or 'active' for the current active model. |
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable. |
| `asOfDate` | `string` | No | - | Last bar to predict (yyyy-MM-dd). Default: the latest stored daily bar. |
| `count` | `integer` | No | default: `1`, min: `1`, max: `64` | Number of consecutive bars to predict. Default 1, at most 64. |

## Example Call

```json
{
  "modelId": "active",
  "symbol": "7203-T",
  "count": 10
}
```

## Common Errors

- `DATA_UNAVAILABLE`: Required workspace, portfolio, notes, model, or drawing file is unavailable.
- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INTERNAL_ERROR`: Unexpected internal tool execution error (see server log).
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
