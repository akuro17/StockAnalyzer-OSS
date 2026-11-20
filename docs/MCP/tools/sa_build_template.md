# `sa_build_template`

- **Mode**: Read-Only

## Contract & Constraints

Builds a new StockAnalyzer template (ScreenerIndicatorTemplate or BacktestConditionTemplate) or merges new conditions into an existing saved template (baseTemplateId), returning both the normalized MCP query representation and the complete persisted StockAnalyzer Template JSON (template object and templateJson string). When saved by the user into Data/Templates/Screener/{id}.json or Data/Templates/BacktestCondition/{id}.json, it functions immediately and seamlessly in the Screener or Backtest modules. templateType selects screener (default) or backtestCondition. For screener: give conditionTree (and optional baseTemplateId, mergeMode appendAnd|appendOr|replace). For backtestCondition: give conditions and/or riskManagement (and optional baseTemplateId, mergeMode appendAnd|appendOr|replace). The tool is strictly read-only and never writes to disk.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `templateType` | `string` | No | enum: `["screener","backtestCondition"]` | Which template kind to build: screener (default) or backtestCondition. |
| `name` | `string` | No | - | Human-readable name of the template (1..200 characters). Required when baseTemplateId is omitted; defaults to base template name otherwise. |
| `description` | `string` | No | - | Optional human-readable description (at most 1000 characters). |
| `templateId` | `string` | No | - | Optional GUID for the template. Defaults to baseTemplateId when provided, or a new GUID otherwise. |
| `baseTemplateId` | `string` | No | - | Optional GUID of an existing saved template to merge or update conditions into (ids from sa_list_templates). |
| `mergeMode` | `string` | No | enum: `["appendAnd","appendOr","replace"]` | How new conditions combine with baseTemplateId: appendAnd (default), appendOr, or replace. |
| `timeFrame` | `string` | No | - | Time frame for conditions that do not specify their own: D1 (default), W1, or MN1. |
| `conditionTree` | `object` | No | - | The Screener condition tree (required when templateType is screener; forbidden for backtestCondition). |
| `conditions` | `object` | No | - | The Backtest conditions object with up to 6 roots (for backtestCondition; forbidden for screener). |
| `riskManagement` | `object` | No | - | The Backtest risk management object: {stopLossRatio?: number, takeProfitRatio?: number} (for backtestCondition; forbidden for screener). |

## Example Call

```json
{
  "templateType": "screener",
  "name": "RSI Oversold",
  "conditionTree": {
    "left": {
      "indicator": "RSI",
      "parameters": {
        "Period": 14
      }
    },
    "comparison": "LessThan",
    "right": 30
  }
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
