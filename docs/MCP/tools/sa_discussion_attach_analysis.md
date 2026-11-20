# `sa_discussion_attach_analysis`

- **Mode**: Discussion (`--enable-discussion`)

## Contract & Constraints

Runs one read-only analysis tool on this server and stores its exact result in the discussion as a numbered context, so every participant discusses identical, hashed data. Use it for numbers instead of typing them from memory; other participants fetch the context with sa_discussion_get_analysis, and a post can refer to it with contextIds. The same tool, arguments and result are stored once (reused true). Execution tools, image tools and discussion tools are refused, and a result larger than maxContextBytes is refused, never cut. The call takes the single tool turn like any analysis and may take as long as a heavy tool; other participants can still read and post meanwhile.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `sessionId` | `string` | Yes | - | The discussion. |
| `participantToken` | `string` | Yes | - | Your token from sa_discussion_join. |
| `tool` | `string` | Yes | - | The analysis tool to run, for example sa_calculate_indicators. |
| `arguments` | `object` | No | - | The arguments of that tool, as described by the tool itself (default: none). |
| `analysisIntent` | `string` | No | - | [Recommended] Purpose or hypothesis of the analysis (e.g. TrendDetection, ReversalDetection, OverboughtOversold, MomentumAnalysis, DivergenceDetection, VolatilityAnalysis, SupportResistance, VolumeStructure, RelativeStrength, RegimeClassification, RiskAssessment). |
| `note` | `string` | No | - | A short remark shown with the context (at most 500 characters). |

## Example Call

```json
{
  "sessionId": "Sabcdef",
  "participantToken": "abcdef.AAAAAAAAAAAAAAAAAAAAAA",
  "tool": "sa_calculate_indicators",
  "arguments": {
    "symbol": "7203.T"
  },
  "analysisIntent": "TrendDetection",
  "note": "daily RSI"
}
```

## Common Errors

- `HUB_UNAVAILABLE`: Discussion hub is disabled or unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
