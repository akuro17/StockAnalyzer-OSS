# `sa_discussion_leave`

- **Mode**: Discussion (`--enable-discussion`)

## Contract & Constraints

Leaves a discussion. Afterwards your token still reads the transcript but can no longer post. Leaving twice is not an error (alreadyLeft is true).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `sessionId` | `string` | Yes | - | The discussion to leave. |
| `participantToken` | `string` | Yes | - | Your token from sa_discussion_join. |

## Example Call

```json
{
  "sessionId": "Sabcdef",
  "participantToken": "abcdef.AAAAAAAAAAAAAAAAAAAAAA"
}
```

## Common Errors

- `HUB_UNAVAILABLE`: Discussion hub is disabled or unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
