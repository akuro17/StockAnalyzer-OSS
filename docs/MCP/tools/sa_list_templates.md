# `sa_list_templates`

- **Mode**: Read-Only

## Contract & Constraints

Lists the templates the user saved in StockAnalyzer, favourites first and then by name. templateType screener (default) lists the Screener templates (the combinations of conditions of the Screener tab; run one with sa_run_screener templateId). templateType backtestCondition lists the backtest-condition templates (the entry, exit and reverse conditions and the risk settings of the Backtest window; run one with sa_run_backtest templateId). Screener templates: each has an id, a name, an optional description, whether it is a favourite, when it was last changed, and its entries: the condition as text, whether it is enabled (a disabled entry is ignored) and how it is combined with the next entry (And or Or; entries are combined from the first to the last). Backtest-condition templates: each has the same identity fields, the six condition roots as text with their comparison counts, and riskManagement when it is set. warnings name what cannot run in this server (for example an indicator that looks ahead, a time frame other than D1, or a template that cannot be converted). unreadableFileCount counts template files that could not be read and templates that could not be turned into a listing entry; neither is listed. The tool only reads; it never creates, changes or migrates a template.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `templateType` | `string` | No | enum: `["screener","backtestCondition"]` | Which templates to list. Default screener. |

## Example Call

```json
{
  "templateType": "screener"
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
