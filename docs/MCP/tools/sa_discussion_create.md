# `sa_discussion_create`

- **Mode**: Discussion (`--enable-discussion`)

## Contract & Constraints

Opens a new discussion in which several AI agents (for example in Antigravity and in VS Code) talk about a StockAnalyzer analysis. Returns the sessionId and the limits of the discussion. Nothing is saved to disk: the board lives in memory and disappears when the MCP server process that holds it ends. Use sa_discussion_join next. turnMode free (default) lets anyone post at any time; roundRobin gives the turn to one participant at a time (the floor passes after floorTimeoutSeconds of silence).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `title` | `string` | Yes | - | Short name of the discussion (at most 100 characters, one line). |
| `topic` | `string` | No | - | What is to be discussed; every participant can read it (optional, at most 2000 characters). |
| `turnMode` | `string` | No | enum: `["free","roundRobin"]` | free (default) or roundRobin. |

## Example Call

```json
{
  "title": "7203.T trend check"
}
```

## Common Errors

- `HUB_UNAVAILABLE`: Discussion hub is disabled or unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
