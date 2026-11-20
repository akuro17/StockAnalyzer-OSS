# `sa_get_model_attention`

- **Mode**: Execution (`--enable-execution`)

## Contract & Constraints

Computes attention weights of the prediction over historical bars for a registered model generation. The bars are read in the model's own time frame (D1, W1 or MN1) and, as on the chart, a running last week or month is included; a W1 or MN1 bar is dated by the start of its period (the Monday, the 1st). Requires --enable-execution. Zero-mutation; results are not persisted.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `modelId` | `string` | Yes | - | The identifier of a registered model generation, or 'active' for the current active model. |
| `symbol` | `string` | Yes | - | Canonical ticker symbol (e.g. 7203-T) whose bars feed the model. |
| `asOfDate` | `string` | No | - | Optional evaluation date (yyyy-MM-dd); defaults to the latest stored bar. |

## Example Call

```json
{
  "modelId": "active",
  "symbol": "7203-T"
}
```

## Common Errors

- `EXECUTION_BUSY`: Maximum concurrent training/execution jobs already running.
- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INTERNAL_ERROR`: Unexpected internal tool execution error (see server log).
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
