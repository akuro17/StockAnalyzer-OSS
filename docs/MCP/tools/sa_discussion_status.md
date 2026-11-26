# `sa_discussion_status`

- **Mode**: Discussion (`--enable-discussion`)

## Contract & Constraints

Lists the discussions on the board (or one) with their state and participants, the limits in effect, and optionally the newest audit entries (what happened, never the text) and the newest tool calls of this server process (names and sizes only). Needs no token and changes nothing. role says whether this process holds the board (hub) or reaches the board of another server process (remote); sharing and sharingKey say whether other clients of this user and data folder use the same board (equal sharingKey = one board). The commands are those of this process only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `sessionId` | `string` | No | - | Only this discussion; leave out to list all. |
| `auditTail` | `integer` | No | default: `0`, min: `0`, max: `100` | How many of the newest audit entries to return. |
| `commandTail` | `integer` | No | default: `0`, min: `0`, max: `100` | How many of the newest tool calls of this process to return. |
| `includeContexts` | `boolean` | No | default: `false`; needs sessionId | true lists the analysis contexts of the discussion given by sessionId (contextId, tool, attachedBy, analysisIntent, resultBytes, sha256, sequence, createdUtc; never the result). Needs sessionId. Default false. |

## Example Call

```json
{}
```

## Common Errors

- `HUB_UNAVAILABLE`: Discussion hub is disabled or unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
