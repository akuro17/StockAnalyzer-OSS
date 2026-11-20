# `sa_discussion_close`

- **Mode**: Discussion (`--enable-discussion`)

## Contract & Constraints

Closes a discussion for everybody. The transcript stays readable for closedRetentionSeconds and is then removed. Closing twice is not an error (alreadyClosed is true).

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `sessionId` | `string` | Yes | - | The discussion to close. |
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
