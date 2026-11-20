# `sa_get_model_explanation`

- **Mode**: Read-Only

## Contract & Constraints

Loads offline explanation (Shapley values, partial dependence) and temperature calibration for a registered model generation. Optionally assigns the market regime when symbol is provided, on bars of the model's own time frame; regimeReason names why none was assigned (null when one was). The explanation, calibration and regime centroids are fixed artefacts of the training run: asOfDate only selects the bars used for the regime. pointInTime gives dataEnd (the last prediction anchor of the training run, null when unknown) and horizonBars (bars of the model's time frame): the labels of the last anchor reach horizonBars bars beyond dataEnd, and asOfBeforeDataEnd is true when asOfDate < dataEnd (null when unknown, never false then). Zero-mutation; contains no file paths.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `modelId` | `string` | Yes | - | The identifier of a registered model generation, or 'active' for the current active model. |
| `symbol` | `string` | No | - | Optional canonical ticker symbol (e.g. 7203-T) to compute regime assignment for. |
| `asOfDate` | `string` | No | - | Optional evaluation date (yyyy-MM-dd); defaults to the latest stored bar. |

## Example Call

```json
{
  "modelId": "active",
  "symbol": "7203-T"
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
