# `sa_search_notes`

- **Mode**: Read-Only

## Contract & Constraints

Searches the user's Ticker Notes (investment journal entries): each note's text, related ticker, hashtags, catalyst date, creation time and thread links, newest first. query holds up to 10 words separated by white space, matched as plain text inside the note text, ignoring case ('%', '_' and '\' are ordinary characters); matchMode All (default) needs every word, Any at least one. ticker (a symbol, '.' and '-' interchangeable), hashtag (with or without '#') and createdFrom/createdTo (yyyy-MM-dd, inclusive, local dates of the creation time) narrow the search; all given conditions must hold. With no condition the newest notes are returned. At most 100 notes are returned (limit); totalMatched counts all matches and truncated says when more exist. includeParentContext true adds a short preview of the note a reply answers (parentContext) and of the note a quote cites (quotedContext). snippetOnly true caps body at 200 characters centered around the first matching word and adds bodyTruncated. Notes in the trash are never returned. Attachments are counted (attachmentCount), not read. Read-only; no database means no notes.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `query` | `string` | No | - | Words to find in the note text, separated by white space (at most 10); plain text, case-insensitive. |
| `matchMode` | `string` | No | enum: `["All","Any"]` | All: every word must be in the note. Any: at least one. Default All. |
| `ticker` | `string` | No | - | Only notes whose related ticker is this symbol. |
| `hashtag` | `string` | No | - | Only notes carrying this hashtag (with or without '#', case-insensitive). |
| `createdFrom` | `string` | No | - | First creation date to include (yyyy-MM-dd). |
| `createdTo` | `string` | No | - | Last creation date to include (yyyy-MM-dd). |
| `limit` | `integer` | No | default: `100`, min: `1`, max: `100` | Most notes returned. Default and maximum 100. |
| `includeParentContext` | `boolean` | No | - | true adds a preview of the replied-to and the quoted note. Default false. |
| `snippetOnly` | `boolean` | No | - | true caps note body at 200 characters and adds bodyTruncated. Default false. |

## Example Call

```json
{
  "query": "earnings guidance",
  "matchMode": "All",
  "ticker": "7203-T",
  "limit": 20
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
