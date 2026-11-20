# `sa_validate_condition_tree`

- **Mode**: Read-Only

## Contract & Constraints

Validates a screener conditionTree or backtest conditions without reading market data. Returns valid:true with structural metrics (node count, depth, rendered comparisons) on success, or valid:false with the first binding error (code, message, path, hint) on invalid input. Read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `kind` | `string` | Yes | enum: `["screener","backtest"]` | The kind of condition to validate ('screener' or 'backtest'). |
| `conditionTree` | `object` | No | - | The condition tree to validate when kind is 'screener'. |
| `conditions` | `object` | No | - | The conditions object to validate when kind is 'backtest'. |
| `timeFrame` | `string` | No | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | The evaluated timeframe for backtest conditions (D1, W1, MN1). Default is D1. |

## Example Call

```json
{
  "kind": "screener",
  "conditionTree": {
    "left": {
      "price": "Close"
    },
    "comparison": "GreaterThan",
    "right": 100
  }
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `UNSUPPORTED_TIMEFRAME`: Requested timeFrame is not supported for stored bars or this tool.
