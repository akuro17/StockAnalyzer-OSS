# `sa_filter_tickers`

- **Mode**: Read-Only

## Contract & Constraints

Filters tickers with rules sent in the call, the same rules and evaluator as a Tickers-tab filter (no saved filter, group or template needed), and optionally a Screener condition. Universe: symbols (a list of symbols), or groupId (a saved ticker group), or neither (every registered symbol); symbols and groupId exclude each other. The universe may hold at most 1000 symbols (LIMIT_EXCEEDED, never cut). rules: at most 32, all must hold (AND). Each rule is {field, operator, value, isCompareToField?}. field is a ticker column name or alias as used by the Tickers tab (for example Close, ChangePercent, Sector, Tag, TrailingPE, MarketCap); an unknown field is INVALID_ARGUMENT, never an empty result. With isCompareToField true, value is another field of the same kind (number with number, text with text). Numeric fields accept: == = Equals Equal != NotEquals NotEqual > GreaterThan >= GreaterThanOrEqual < LessThan <= LessThanOrEqual, and value must be a number. Text fields accept: *= Contains !*= NotContains !Contains DoesNotContain == = Equals Equal != NotEquals NotEqual (text comparison ignores case). The Tag field accepts: *= Contains ContainsTag !*= NotContains !Contains DoesNotContain == = Equals Equal != NotEquals NotEqual (tags are separated by comma or semicolon). ComparisonOperator enum spellings are normalised case-insensitively via ComparisonOperatorExtensions.ToSymbolString (integer strings remain refused). A field that depends on daily bars (Open, High, Low, Close, Volume, Change, ChangePercent, valuation ratios built on the price) uses the latest stored daily bar and the one before it; a symbol without stored bars has no price, so it matches no such rule. Values keep the unit of the Tickers-tab column: ChangePercent is a percent value (1.5 means 1.5 %), prices are in the currency of the stored data. screener is {conditionTree} in the grammar of sa_run_screener (see its description and sa_get_capabilities with includeScreenerCatalog true); it is applied after the rules, to the symbols the rules kept, on the latest daily bar exactly as sa_run_screener does, and at most 500 symbols may reach it (LIMIT_EXCEEDED: narrow the universe or the rules). Matches are sorted by symbol (ordinal, ascending) and paged with offset and limit. includeFields adds those columns to each returned match (null when the value is not available). storeResult true stores the returned symbols of this page in memory and emits resultId (for downstream tools accepting symbolsFromResultId); default false. Without rules and screener the whole universe is returned. Without allowUnregistered, symbols that are not in the ticker list are counted in universe.unregisteredCount and left out. allowUnregistered true evaluates the listed symbols that are not in the ticker list but have stored daily bars (named in warnings); a listed symbol without stored bars is SYMBOL_NOT_FOUND. It applies to symbols only, not to groupId or the whole-list universe. The result is a snapshot of today's stored data (the latest stored bars and the stored metadata), not of a past date: do not use it to build the universe of an analysis dated in the past; for that use sa_run_screener with asOfDate, which refuses snapshot columns.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbols` | `array` | No | - | Symbols to filter, 1 to 1000. Excludes groupId. Omit both to filter every registered symbol. |
| `groupId` | `string` | No | - | Guid of a ticker group from sa_list_ticker_groups; its members are the universe. Excludes symbols. |
| `rules` | `array` | No | - | Filter rules, at most 32; all must hold. Omit or send [] for no rule. |
| `screener` | `object` | No | - | A Screener condition applied after the rules, to the symbols they kept. |
| `includeFields` | `array` | No | - | Ticker columns to add to each returned match; each must be a known column name or alias. |
| `offset` | `integer` | No | default: `0`, min: `0` | Number of matches to skip. Default 0. |
| `limit` | `integer` | No | default: `100`, min: `1`, max: `1000` | Page size. Default 100, at most 1000. |
| `allowUnregistered` | `boolean` | No | - | Evaluate listed symbols that are not in the ticker list but have stored daily bars. Applies to symbols only. Default false. |
| `storeResult` | `boolean` | No | - | Store all matching symbols in memory and return resultId for downstream tools. Default false. |
| `includeMatches` | `boolean` | No | - | Include matches in the response. Default true; false requires storeResult true and cannot be combined with includeFields. |

## Example Call

```json
{
  "symbols": [
    "MSFT",
    "AAPL"
  ],
  "rules": [
    {
      "field": "Close",
      "operator": "\u003E",
      "value": "100"
    }
  ],
  "screener": {
    "conditionTree": {
      "left": {
        "indicator": "RSI",
        "parameters": {
          "Period": 14
        }
      },
      "comparison": "LessThan",
      "right": 70
    }
  },
  "includeFields": [
    "Close",
    "ChangePercent"
  ],
  "limit": 50
}
```

## Common Errors

- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
