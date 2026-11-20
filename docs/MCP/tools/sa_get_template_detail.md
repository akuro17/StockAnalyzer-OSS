# `sa_get_template_detail`

- **Mode**: Read-Only

## Contract & Constraints

Returns the detail of one saved template: its identity fields (id, name, description, isFavorite, updatedAt) and its conditions in MCP JSON syntax, so that the client can inspect them or pass them directly to sa_run_screener (conditionTree) or sa_run_backtest (conditions and riskManagement). templateType selects screener (default) or backtestCondition. If any template entries cannot be converted to MCP syntax, unconvertibleEntries lists their indices and reasons. The tool only reads; it never creates, changes or migrates a template.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `templateType` | `string` | No | enum: `["screener","backtestCondition"]` | Which template kind to read. Default screener. |
| `templateId` | `string` | Yes | - | Unique identifier (GUID) of the saved template from sa_list_templates. |

## Example Call

```json
{
  "templateType": "screener",
  "templateId": "00000000-0000-0000-0000-000000000000"
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `TEMPLATE_NOT_FOUND`: No saved template matches the given templateType and templateId.
