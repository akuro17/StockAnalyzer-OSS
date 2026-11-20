# `sa_run_screener`

- **Mode**: Read-Only

## Contract & Constraints

Finds the symbols whose latest bar meets a condition, with the application's own Screener (the same evaluation as the Screener tab for the daily time frame). Call sa_get_capabilities with includeScreenerCatalog true first to learn the column, criteria and price names; indicator types, parameters and series come from sa_get_capabilities. Give exactly one of conditionTree (a condition you write) or templateId (a Screener template the user saved; ids come from sa_list_templates). conditionTree is a group {operator: And|Or, children: [...]} of groups and comparisons (or one comparison). A comparison is {left: side, comparison: GreaterThan|GreaterThanOrEqual|LessThan|LessThanOrEqual|Equal|NotEqual|Contains|DoesNotContain, right: number | text | side}. Besides those, CrossesAbove, CrossesBelow, Rising, Falling are accepted as comparison and are expanded exactly into the comparisons above, joined by And (the result shows the expansion): CrossesAbove = left > right now and left <= right one bar earlier; CrossesBelow = left < right now and left >= right one bar earlier (a number as right stays that number); Rising and Falling take bars N (1 up to the largest offset) instead of right: every one of the last N bar-to-bar steps of left goes up (Rising) or down (Falling), that is left[0] > left[1] and left[1] > left[2] ... and left[N-1] > left[N] for Rising. The expanded comparisons count against the tree-size limits; an offset that the expansion would push past the largest offset is INVALID_ARGUMENT. A side names exactly one of indicator (with optional parameters and outputSeriesName), price, column or criteria, plus an optional offset (bars back from the latest bar of the side's own time frame, default 0) and an optional timeFrame (see below). Example: {"operator":"And","children":[{"left":{"indicator":"RSI","parameters":{"Period":14}},"comparison":"LessThan","right":30},{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":200}}}]}. A criteria side is 1 when the pattern is present at the bar and 0 otherwise (compare it with Equal 1). A text column (Sector, Industry, Region, Name, Tag) is compared with text using Equal, NotEqual, Contains or DoesNotContain. A comparison whose value cannot be calculated (too little history, no metadata) does not match. Indicators that look ahead are not allowed. An indicator that compares two symbols (isCrossTicker in sa_get_capabilities, for example Correlation) needs its ComparisonSymbol parameter: a stored symbol, at most 4 different ones per call; a symbol screened against itself is compared with itself, and the comparison symbol's bars are matched to each screened symbol's dates. Give symbols (at most 500) or groupId to screen a specific universe; omit both to screen every available symbol. The tree size and offset limits are the application's Screener settings (see sa_get_settings). matchedSymbols are in ascending order, at most maxReturnedMatches (default and maximum 200); matchedSymbolCount is the true count and truncated says whether the list was cut. storeResult true stores the returned matchedSymbols in memory and emits resultId (for downstream tools accepting symbolsFromResultId); default false. sortBy ranks the matched symbols by one side (an indicator, price or numeric column with optional parameters, outputSeriesName and offset, evaluated exactly like a comparison side; a criteria side or a text column is INVALID_ARGUMENT) in sortDirection Descending (default) or Ascending; the list is then ranked, not alphabetical: matchedSymbols follow ranking, ranking is [{rank, symbol, sortValue}] (rank 1 first; sortValue is the side's value in its own unit, null where it cannot be calculated), a symbol without a value comes after all that have one in either direction, equal values are in ascending symbol order, and maxReturnedMatches keeps the first ranks while matchedSymbolCount stays the true count. Without sortBy there is no ranking key and matchedSymbols stay ascending. Ranking reads the sort side of every matched symbol. includeMatchedValues adds the value of each comparison's two sides for every returned symbol. timeFrame D1 (default), W1 or MN1: a weekly or monthly bar is built from the stored daily bars (the days of the period, dated by their first day), and periods in conditions are in bars of that frame. A last W1 or MN1 period is partial while its last calendar day (the Sunday of the week, the last day of the month) is after the reference date (asOfDate, or the last stored day), the rule of sa_get_candles; completeBarsOnly true (default) evaluates the previous complete period instead and skippedPartialBarCount says for how many symbols, false evaluates the partial period and sets partialLastBar true with its partialPeriodStarts. Each side may name its own timeFrame (D1, W1 or MN1; sortBy too), so one condition can mix frames, for example the daily close against a weekly SMA: {"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":10},"timeFrame":"W1"}}. As soon as one side (or sortBy, or a saved template that stores a weekly or monthly side) names a frame, every side reads its own frame: a side without timeFrame reads the tool-level timeFrame (default D1), and a template reads the frames it stores (then the tool-level timeFrame must be D1 or omitted). With no side naming a frame the whole scan is on the tool-level timeFrame as above. A side's period and offset count bars of its own frame. completeBarsOnly and asOfDate apply to every weekly or monthly side. A scan in which sides read their own frames reports each weekly or monthly frame it read in sideFrames [{timeFrame, partialLastBar, partialPeriodStarts, skippedPartialBarCount, symbolsWithoutBarsCount, symbolsWithoutBars}] and has no top-level partialLastBar, partialPeriodStarts or skippedPartialBarCount. INVALID_ARGUMENT (param <side>.timeFrame): a timeFrame on a column read from the stored metadata (only Open, High, Low, Close, Volume, Change and Change % have bars), a frame other than D1 on AnchoredVWAP or on an indicator that compares two symbols; an indicator that compares two symbols is not accepted at all in a scan in which a side names its timeFrame. asOfDate (yyyy-MM-dd, inclusive) evaluates every symbol on its last bar dated on or before that date, reading no later bar (comparison symbols and includeMatchedValues too); a symbol with no bar on or before it is not evaluated and is counted in noDataAtAsOfCount instead of evaluatedSymbolCount. With asOfDate a column side must be one read from the bars (Change %, Change, Close, High, Low, Open, Volume): every other column is today's stored snapshot and is refused with INVALID_ARGUMENT. asOfDate removes look-ahead only, not survivorship bias: the screened universe is today's ticker list (symbols delisted since are absent), and the members of a groupId are today's. allowUnregistered true also accepts symbols (and comparison symbols) that are not in the ticker list but have stored daily bars; they are named in warnings, and a symbol without stored bars is SYMBOL_NOT_FOUND. It applies to explicit symbols only, not to groupId. A template is run as the user saved it (enabled entries combined from the first to the last by the And or Or of the preceding entry); entries that can never match here are listed in warnings. The tool never changes anything and never fetches data.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `conditionTree` | `object` | No | - | The condition tree (see the tool description). Give this or templateId. |
| `templateId` | `string` | No | - | Id of a saved Screener template (from sa_list_templates). Give this or conditionTree. |
| `symbols` | `array` | No | - | Symbols to screen (at most 500); omit to screen every available symbol. Mutually exclusive with groupId. |
| `groupId` | `string` | No | - | Guid of a saved ticker group (from sa_list_ticker_groups) to screen. Mutually exclusive with symbols. |
| `timeFrame` | `string` | No | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars of every side that names no timeFrame of its own. Supported: D1, W1, MN1. Default D1. |
| `completeBarsOnly` | `boolean` | No | - | W1 and MN1 only (ignored for D1): true (default) evaluates the last complete period when the latest one has not ended; false evaluates the partial period and says so (partialLastBar). |
| `sortBy` | `object` | No | - | Rank the matched symbols by one side: {indicator\|price\|column: name, parameters?, outputSeriesName?, offset?} (the same side object as in conditionTree; not criteria or a text column). |
| `sortDirection` | `string` | No | enum: `["Descending","Ascending"]` | Descending (default: larger value first) or Ascending. Needs sortBy. |
| `asOfDate` | `string` | No | - | Evaluation date (yyyy-MM-dd, inclusive): each symbol is evaluated on its last bar on or before it. Default: the latest stored bar. Columns other than those read from the bars are refused with it. |
| `includeMatchedValues` | `boolean` | No | - | Add the values each comparison read, per returned symbol. Default false. |
| `allowUnregistered` | `boolean` | No | - | Accept symbols that are not in the ticker list but have stored daily bars. Default false. |
| `maxReturnedMatches` | `integer` | No | default: `200`, min: `1`, max: `200` | Most matched symbols to return. Default and maximum 200. |
| `storeResult` | `boolean` | No | - | Store all matched symbols in memory and return resultId for downstream tools. Default false. |
| `includeMatchedSymbols` | `boolean` | No | - | Include matchedSymbols (and ranking) in the response. Default true; false requires storeResult true and cannot be combined with includeMatchedValues. |

## Example Call

```json
{
  "timeFrame": "D1",
  "conditionTree": {
    "left": {
      "price": "Close"
    },
    "comparison": "GreaterThan",
    "right": {
      "indicator": "SMA",
      "parameters": {
        "Period": 10
      },
      "timeFrame": "W1"
    }
  },
  "maxReturnedMatches": 50
}
```

## Common Errors

- `CONDITION_LIMIT_EXCEEDED`: Condition tree depth or node count exceeds server limits.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
- `SYMBOL_NOT_FOUND`: Symbol has no stored market data (or is unregistered when allowUnregistered is false).
- `TEMPLATE_NOT_FOUND`: No saved template matches the given templateType and templateId.
