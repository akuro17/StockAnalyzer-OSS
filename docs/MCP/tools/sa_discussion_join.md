# `sa_discussion_join`

- **Mode**: Discussion (`--enable-discussion`)

## Contract & Constraints

Joins a discussion under a display name and returns a private participantToken (keep it secret: every later call needs it) and a short handle such as p1. The result also holds the rules of the discussion (protocol) and its current state. A name that has left or has been away can join again: it keeps its handle and receives a new token, the old one stops working.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `sessionId` | `string` | Yes | - | The sessionId returned by sa_discussion_create (or listed by sa_discussion_status). |
| `displayName` | `string` | Yes | - | Your name in this discussion (at most 64 characters, one line); a label for the others, not an identity. |

## Example Call

```json
{
  "sessionId": "Sabcdef",
  "displayName": "Claude"
}
```

## Common Errors

- `HUB_UNAVAILABLE`: Discussion hub is disabled or unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
