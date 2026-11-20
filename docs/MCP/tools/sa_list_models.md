# `sa_list_models`

- **Mode**: Read-Only

## Contract & Constraints

Lists registered supervised and reinforcement learning model generations, sorted by creation date descending. kind filters by 'All', 'Supervised', or 'ReinforcementLearning'. symbol optionally filters to models applicable to or trained on a specific canonical symbol. Contains zero file paths.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `kind` | `string` | No | enum: `["All","Supervised","ReinforcementLearning"]` | Filter by model kind: 'All', 'Supervised', or 'ReinforcementLearning'. Default 'All'. |
| `symbol` | `string` | No | - | Optional canonical ticker symbol (e.g. 7203-T) to filter models by. |
| `offset` | `integer` | No | default: `0`, min: `0` | Number of models to skip. Default 0. |
| `limit` | `integer` | No | default: `100`, min: `1`, max: `100` | Page size. Default 100, at most 100. |

## Example Call

```json
{
  "kind": "All",
  "limit": 20
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
