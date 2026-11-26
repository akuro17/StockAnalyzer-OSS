# `sa_discussion_get_analysis`

- **Mode**: Discussion (`--enable-discussion`)

## Contract & Constraints

Returns one analysis context attached with sa_discussion_attach_analysis: the tool, its arguments, who attached it, the sha256 of the result and the result itself. The result is the exact text the analysis tool produced (number text unchanged), so every participant sees the same data; equal sha256 values mean equal data. Set includeResult false to get only the description. A participant who has left may still read contexts. Pass paths to get only some fields of the result (extracted, with missingPaths for the paths that do not exist) instead of the whole result, which may be too large to receive.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `sessionId` | `string` | Yes | - | The discussion. |
| `participantToken` | `string` | Yes | - | Your token from sa_discussion_join. |
| `contextId` | `integer` | Yes | min: `1` | The context number from sa_discussion_attach_analysis, a message of kind analysis, or the contextIds of a message. |
| `includeResult` | `boolean` | No | - | false returns the description without the result (default true). |
| `paths` | `array` | No | min items: `1`; at most 16 different paths of at most 128 characters | Fields of the result to return instead of the whole result (not with includeResult false). A path is a name (letters, digits, underscore) followed by .name, [index] (0 to 999999) or ["key"] parts, for example summary.latest, series[0].value or matchedValues["7203-T"].left; write a key with other characters between double quotes and escape a quote or a backslash in it with a backslash. A number keeps its exact text. |

## Example Call

```json
{
  "sessionId": "Sabcdef",
  "participantToken": "abcdef.AAAAAAAAAAAAAAAAAAAAAA",
  "contextId": 1
}
```

## Common Errors

- `HUB_UNAVAILABLE`: Discussion hub is disabled or unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
