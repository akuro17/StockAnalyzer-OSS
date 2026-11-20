# `sa_start_training`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Starts one supervised training job (the Training Wizard's path) on stored daily bars and returns at once with a jobId; poll sa_get_training_status until the state is final. The model is written to a temporary folder that is removed when the server stops: nothing is saved under Data and nothing is registered. Fixed by the server: daily bars, OHLCV min-max features, a fresh model, no ensemble. Frameworks, architectures and hyperparameters (names, defaults, ranges) are listed in sa_get_capabilities under execution. Only a limited number of jobs run at once (EXECUTION_BUSY). A server whose temporary folder path is too long for the trainer is refused before anything is created (SANDBOX_PATH_TOO_LONG).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | Yes | - | Symbols to train on, 1 to 100, none twice ('.' and '-' are interchangeable). |
| `startDate` | `string` | No | - | First date of the training data (yyyy-MM-dd). Default: the start of the stored data. |
| `endDate` | `string` | No | - | Last date of the training data (yyyy-MM-dd). Default: the end of the stored data. |
| `framework` | `string` | Yes | enum: `["PyTorch","LightGBM","TensorFlow"]` | Training framework. |
| `architecture` | `string` | Yes | - | Architecture of the framework (see sa_get_capabilities, execution.architectures). |
| `windowSize` | `integer` | Yes | - | Input length in bars. |
| `horizon` | `integer` | Yes | - | How many bars ahead the label looks. |
| `targetType` | `string` | No | enum: `["Classification","Regression"]` | Learning objective. Default Classification. |
| `nSplits` | `integer` | No | default: `5` | Number of walk-forward splits. |
| `fixedZScore` | `boolean` | No | - | Standardize features with population Z-score. Default false. |
| `clipSigma` | `number` | No | - | Optional positive outlier clipping multiple after Z-score (for example 3.0). |
| `calibration` | `boolean` | No | - | Fitted temperature probability calibration for classification. Default false. |
| `cpcv` | `boolean` | No | - | Combinatorial purged cross-validation evaluation. Default false. |
| `hyperparameters` | `object` | No | - | Trainer hyperparameters by key (see sa_get_capabilities, execution.hyperparameters); a value is a string or a number. A key left out keeps the trainer's default. |

## Example Call

```json
{
  "symbols": [
    "7203-T"
  ],
  "framework": "LightGbm",
  "architecture": "LightGbmStandard",
  "windowSize": 30,
  "horizon": 5
}
```

## Common Errors

- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
