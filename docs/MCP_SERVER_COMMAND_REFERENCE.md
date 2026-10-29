# StockAnalyzer.McpServer Command & Tool Reference

## 1. System Overview & Architecture

`StockAnalyzer.McpServer` is a dedicated, platform-agnostic .NET 8.0 console process providing direct programmatic access to the StockAnalyzer domain and calculation engine via the Model Context Protocol (MCP).

```
+-----------------------------------------------------------------------------------+
|                            Host Application / Client                              |
+-----------------------------------------------------------------------------------+
                                   |  ^
                Standard Input     |  | Standard Output
              (JSON-RPC 2.0 UTF-8) |  | (JSON-RPC 2.0 UTF-8)
                                   v  |
+-----------------------------------------------------------------------------------+
|                            StockAnalyzer.McpServer                                |
|                                                                                   |
|  [CommandLine Parser] ---> [McpServerHost] <---> [McpMessageDispatcher]           |
|                                                         |                         |
|                               +-------------------------+----------------------+  |
|                               |                                                |  |
|                               v                                                v  |
|                     [McpResourceRegistry]                             [McpToolRegistry]  |
|                     - catalog/indicators                              - 16 Read-Only Tools|
|                     - catalog/screener                                - 7 Execution Tools |
|                     - settings/current                                  (--enable-execution)
|                               |                                                |  |
+-------------------------------|------------------------------------------------|--+
                                |                                                |
                                v                                                v
               +----------------------------------+             +-------------------+
               | StockAnalyzer Core Domain Engine |             | Ephemeral Sandbox |
               | (Zero mutation on Data directory)|             | (OS Temp Folder)  |
               +----------------------------------+             +-------------------+
```

### 1.1 Technical Specifications
- **Target Runtime**: .NET 8.0 (C# 12)
- **Transport**: Standard Input/Output (`stdio`).
- **Stream Purity**: `Console.Out` is redirected to `Console.Error` at startup. `Console.OpenStandardOutput()` is retained strictly for JSON-RPC 2.0 framing.
- **Protocol Versions**: `2025-11-25`, `2025-06-18` (via `McpProtocolVersions.Negotiate`). The stateless `2026-07-28` protocol revision is intentionally not supported.
- **Data Integrity**: Under default operation, all read operations are strictly non-mutating against the `Data/` directory.

---

## 2. Command-Line Interface (CLI) Specification

### 2.1 Invocation Syntax
```shell
StockAnalyzer.McpServer.exe [options]
```

### 2.2 CLI Tokens
CLI tokens are case-sensitive and parsed strictly from left to right.

| Option Token | Arguments | Description | Constraints & Invariants |
|---|---|---|---|
| `--help`, `-h`, `-?`, `/?` | `[tool]` (optional) | Prints help text and exits. When `[tool]` is omitted, outputs general overview. When `[tool]` is provided, outputs schema and argument specification for the specified tool. | If a subsequent token begins with `-` or `/`, it is parsed as a new option rather than a tool name. |
| `--version` | None | Prints binary informational version and supported MCP protocol versions. | Must be the only argument. Combining with any other token results in exit code `64`. |
| `--enable-execution` | None | Enables opt-in sandboxed execution tools (model training, ONNX prediction, reinforcement learning). | Operates in an OS temporary sandbox directory (`EphemeralSandbox.Root`). Data directory is never modified. |

### 2.3 Process Exit Codes

| Exit Code | Constant | Meaning | Trigger Conditions |
|---|---|---|---|
| `0` | `ExitOk` | Successful execution | Normal process termination, completed `--help`, or completed `--version`. |
| `2` | `ExitInvalidConfiguration` | Configuration limit outside hard band | A configured property in `AiIntegration` or `SandboxedExecution` violates invariant bounds defined in `McpLimitsContract`. |
| `64` | `ExitInvalidUsage` | Invalid CLI invocation | Unrecognized CLI argument, conflicting arguments, or invalid tool name requested in `--help <tool>`. |

---

## 3. Configuration Limits (`AiIntegration` / `McpLimits`)

Tunable parameters configured in `appsettings.json` under section `AiIntegration`. Bound values must strictly remain within the hard invariant boundaries verified at process startup.

| Parameter Name | Type | Default | Hard Min | Hard Max | Description |
|---|---|---|---|---|---|
| `MaxCandlesPerRequest` | `int` | `10000` | `1` | `50000` | Maximum number of OHLCV bars returned or processed in a single tool call. |
| `DefaultCandleCount` | `int` | `200` | `1` | `MaxCandlesPerRequest` | Default bar count when `count` argument is omitted in `sa_get_candles`. |
| `MaxIndicatorsPerCall` | `int` | `16` | `1` | `64` | Maximum number of distinct indicator instances calculated in a single `sa_calculate_indicators` call. |
| `MaxSymbolsPerMetadataCall` | `int` | `100` | `1` | `500` | Maximum number of ticker symbols accepted in a single `sa_get_ticker_metadata` call. |
| `MaxReturnedTrades` | `int` | `200` | `0` | `1000` | Maximum number of closed trades returned in `sa_run_backtest`. |
| `MaxReturnedWarnings` | `int` | `20` | `0` | `200` | Maximum number of execution warning messages returned in `sa_run_backtest`. |
| `MaxOutputTailBars` | `int` | `5000` | `1` | `MaxCandlesPerRequest` | Maximum number of trailing calculated bars returned in indicator or chart series calls. |
| `DefaultOutputTailBars` | `int` | `200` | `1` | `MaxOutputTailBars` | Default trailing bar count when `outputTailCount` is omitted. |
| `ToolTimeoutSeconds` | `int` | `60` | `1` | `900` | Cancellation timeout for standard read-only tools. |
| `HeavyToolTimeoutSeconds` | `int` | `180` | `ToolTimeout` | `1800` | Cancellation timeout for heavy computational tools (`sa_run_screener`, `sa_run_backtest`, `sa_filter_tickers`). |
| `QueueWaitSeconds` | `int` | `60` | `0` | `600` | Maximum time a request waits in the internal execution queue before timeout. |
| `MaxResponseBytes` | `int` | `4194304` | `65536` | `8388608` | Maximum byte size of a serialized JSON-RPC response payload (default: 4 MB, max: 8 MB). |
| `MaxRequestBytes` | `int` | `1048576` | `4096` | `8388608` | Maximum byte size of an incoming JSON-RPC request line (default: 1 MB, max: 8 MB). |
| `MaxScreenerSymbols` | `int` | `500` | `1` | `5000` | Maximum number of symbols evaluated during an explicit universe screener run; also the most symbols that may reach the screener of one `sa_filter_tickers` call (the symbols its rules kept). |
| `MaxReturnedMatches` | `int` | `200` | `1` | `1000` | Maximum number of matched symbols returned by `sa_run_screener`. |
| `MaxSecondarySymbolsPerCall` | `int` | `4` | `1` | `8` | Maximum number of distinct comparison symbols allowed for cross-ticker indicators. |
| `MaxComparisonTargets` | `int` | `10` | `1` | `10` | Maximum number of distinct comparison symbols in one `sa_calculate_relative_performance` call (the primary symbol and repeats are not counted). Exceeding it raises `LIMIT_EXCEEDED`. |
| `DefaultTickerPageSize` | `int` | `100` | `1` | `MaxTickersPerPage` | Default page size when `limit` is omitted in `sa_list_tickers` and `sa_filter_tickers`. |
| `MaxTickersPerPage` | `int` | `1000` | `1` | `5000` | Maximum page size allowed in `sa_list_tickers` and `sa_filter_tickers`. |
| `MaxGroupNodes` | `int` | `500` | `1` | `5000` | Maximum number of group nodes returned by `sa_list_ticker_groups`. |
| `MaxFilterEvaluationSymbols` | `int` | `1000` | `1` | `5000` | Maximum number of symbols evaluated when filtering ticker lists; for `sa_filter_tickers` the most symbols of its universe (more raises `LIMIT_EXCEEDED`, never cut). |
| `MaxInlineFilterRules` | `int` | `32` | `1` | `128` | Maximum number of rules in one `sa_filter_tickers` call (more raises `LIMIT_EXCEEDED`, never cut). |

---

## 4. MCP Resources Specification

URI Scheme: `stockanalyzer://`

| Resource URI | MIME Type | Name | Content Description |
|---|---|---|---|
| `stockanalyzer://catalog/indicators` | `application/json` | Indicator catalogue | Every indicator type with its parameters (default, range), output series and flags; the same content as `sa_get_capabilities` with `includeDetails`, except its `execution` block. |
| `stockanalyzer://catalog/screener` | `application/json` | Screener catalogue | The columns, patterns, price sources and operators `sa_run_screener` accepts, and the columns it cannot read. |
| `stockanalyzer://settings/current` | `application/json` | Current settings | The allow-listed application settings (limits and defaults); the same content as `sa_get_settings`. File locations and credentials are never included. |

---

## 5. Error Handling & Error Codes

### 5.1 JSON-RPC Protocol Faults vs. Tool Execution Failures
- **JSON-RPC Errors**: Reserved for transport, framing, or JSON parsing failures (`ParseError: -32700`, `InvalidRequest: -32600`, `MethodNotFound: -32601`, `InvalidParams: -32602`, `InternalError: -32603`). An execution tool called without `--enable-execution` is `-32602`.
- **Tool Failures**: Domain, logic, or calculation errors return a valid JSON-RPC result with `isError: true`. `structuredContent` (and the text block) carries `{ "code": string, "message": string, "param": string?, "hint": string? }`; `param` names the argument (nested names are `name[i].key`), `hint` states how to correct the call. Messages never contain absolute paths.

### 5.2 Tool Error Codes (`ToolErrorCodes`)

| Error Code | Scope | Description |
|---|---|---|
| `INVALID_ARGUMENT` | All Tools | Argument syntax violation, unknown argument, out-of-range value, or logical contradiction (e.g., `startDate > endDate`). |
| `SYMBOL_NOT_FOUND` | Symbol Tools | Requested ticker symbol is not registered or cannot be resolved. |
| `UNSUPPORTED_TIMEFRAME` | Timeframe Tools | Requested timeframe is not supported by the invoked tool (e.g., an intraday frame such as `M5` on `sa_run_screener`). |
| `UNKNOWN_INDICATOR` | Indicator Tools | Requested indicator type name does not exist in `IndicatorCatalog`. |
| `INVALID_INDICATOR_PARAMETER` | Indicator Tools | Specified parameter name is invalid or value is outside `CoreParameterRange`. |
| `INVALID_OUTPUT_SERIES` | Indicator Tools | Requested output series name does not exist on the specified indicator. |
| `CONDITION_LIMIT_EXCEEDED` | Screener / Backtest | Condition tree depth, node count, or offset exceeds configured limits. |
| `LIMIT_EXCEEDED` | All Tools | A requested count (symbols, items, bars, nodes) exceeds the `McpLimits` allowance, or the whole result is larger than `MaxResponseBytes`. |
| `INSUFFICIENT_MARKET_DATA` | Calculation Tools | Stored candle history has fewer bars than required for the calculation. |
| `TEMPLATE_NOT_FOUND` | Template Tools | Specified template GUID does not exist. |
| `TEMPLATE_MIGRATION_PENDING` | Template Tools | Referenced template is in a legacy format requiring GUI migration before CLI execution. |
| `EXECUTION_TIMEOUT` | All Tools | Tool execution exceeded `ToolTimeoutSeconds` or `HeavyToolTimeoutSeconds`. |
| `SERVER_BUSY` | All Tools | Request concurrency limit reached. |
| `FORBIDDEN_OPERATION` | Security | Attempted operation violates read-only or boundary invariants. |
| `DATA_UNAVAILABLE` | Gateway | Market data store is incomplete or inaccessible. |
| `GROUP_NOT_FOUND` | Group Tools | Ticker group GUID was not found in the workspace. |
| `INTERNAL_ERROR` | Core Engine | Unhandled domain exception. Details are logged to stderr; payload returns generic message. |
| `EXECUTION_BUSY` | Execution Tools | Maximum concurrent training or prediction jobs exceeded. |
| `JOB_NOT_FOUND` | Execution Tools | Specified training job GUID does not exist. |
| `ARTIFACT_NOT_FOUND` | Execution Tools | Model artifact or checkpoint was not found. |
| `PYTHON_ENV_MISSING` | Execution Tools | Python runtime or required ML dependencies are unavailable. |
| `SANDBOX_PATH_TOO_LONG` | Execution Tools | Temporary sandbox path exceeds filesystem length constraints. |

### 5.3 Codes Every Tool Can Return
`INVALID_ARGUMENT` (schema check), `EXECUTION_TIMEOUT`, `SERVER_BUSY`, `INTERNAL_ERROR`, `DATA_UNAVAILABLE`, and `LIMIT_EXCEEDED` for an over-large result. The **Error Codes** line of each tool below lists the codes specific to that tool in addition.

---

## 6. Read-Only Tools Reference (16 Tools)

All read-only tools are marked read-only and non-destructive in `tools/list`. Every `timeFrame` schema lists all time-frame names, but only `D1`, `W1` and `MN1` are accepted (`UNSUPPORTED_TIMEFRAME` otherwise). The date that ends a calculation is `endDate` for tools that return a series over a window and `asOfDate` for tools that evaluate one bar per symbol or predict (`sa_run_screener`, `sa_run_onnx_prediction`, `sa_get_rl_playback`); there is no alias, and the other spelling is `INVALID_ARGUMENT` (for `sa_calculate_indicators` with `asOfDate` and `sa_run_screener` with `endDate` the hint names the right argument). With a date a symbol is read as it was known on that date (the daily series is cut before `W1` and `MN1` bars are built); a date removes look-ahead only, not survivorship bias: the universe is today's ticker list. Heavy tools run under `HeavyToolTimeoutSeconds` (default 180 s), the others under `ToolTimeoutSeconds` (default 60 s).

### 6.1 `sa_list_tickers`
Lists registered ticker symbols (All Tickers), ordinally sorted. Canonical format: uppercase with `-` (e.g., `7203-T`).

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `offset` | `integer` | No | `0` | Min: `0` | - | Number of matching tickers to skip. |
| `limit` | `integer` | No | `100` | Min: `1`, Max: `MaxTickersPerPage` | - | Page size. |
| `searchQuery` | `string` | No | `null` | - | - | Case-insensitive substring match against symbol or short name. |
| `includeMetadataSummary` | `boolean` | No | `false` | - | - | Inlines company identity fields. At most `MaxSymbolsPerMetadataCall` tickers per call. |
| `groupId` | `string` | No | `null` | - | - | GUID of a saved workspace group from `sa_list_ticker_groups`; lists only its members. |

- **Boundary Conditions**: If `offset` exceeds total matching symbols, an empty array is returned with `hasMore: false`. Dynamic filter groups load daily candles lazily and are bounded by `MaxFilterEvaluationSymbols`.
- **Return Fields**: `groupId?`, `groupKind?`, `groupUnregisteredMemberCount?`, `groupUnknownRuleFields?` (only with `groupId`), `totalCount`, `offset`, `limit`, `returnedCount`, `hasMore`, `tickers` (array of `{symbol, hasMetadata, identity fields when a summary is requested}`).
- **Error Codes**: `GROUP_NOT_FOUND`, `LIMIT_EXCEEDED`.

---

### 6.2 `sa_list_ticker_groups`
Lists workspace ticker groups (Watchlists, Portfolios, Custom Categories, List Folders, and Tickers-tab filters).

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `includeRules` | `boolean` | No | `false` | - | - | Inlines configured filter rules for filter nodes. |

- **Boundary Conditions**: If no workspace file exists, `workspaceFound` is `false` and the list is empty. Nodes are listed pre-order, depth first; a tree deeper than the fixed depth limit is truncated (`treeTruncatedAtDepth`).
- **Return Fields**: `workspaceFound`, `nodeCount`, `droppedFilterCount`, `treeTruncatedAtDepth`, `groups` (flat array of `{id, parentId, kind, name, depth, memberCount, unregisteredMemberCount, ruleCount, rules?[{field, operator, value, enabled, isNegated, caseSensitive}]}`; static groups have `ruleCount` null, filters have `memberCount` and `unregisteredMemberCount` null).
- **Error Codes**: `LIMIT_EXCEEDED` (more than `MaxGroupNodes` nodes; the tree is never cut).

---

### 6.3 `sa_get_candles`
Returns chronological OHLCV bars for one symbol and timeframe (`D1`, `W1`, `MN1`), exactly as stored.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol (`.` and `-` interchangeable). |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Bar duration. |
| `count` | `integer` | No | `200` | Min: `1`, Max: `MaxCandlesPerRequest` | - | Bar count. |
| `startDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Inclusive start date. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Inclusive end date. |
| `completeBarsOnly` | `boolean` | No | `false` | - | - | `W1`/`MN1` only: drops a last bar whose period has not ended. |

- **Boundary Conditions**: `startDate > endDate` raises `INVALID_ARGUMENT`. With dates, bars are filtered inside the latest `MaxCandlesPerRequest` bars and then the latest `count` are returned; for `W1`/`MN1` with `endDate` the daily bars are cut at `endDate` before the period bars are built. If fewer bars exist than `count`, all are returned. A period counts as complete only when `referenceDate` reaches its last calendar day (no market calendar, no clock).
- **Return Fields**: `symbol`, `timeFrame`, `periodUnit`, `timestampFormat`, `units{price, volume}`, `barCount`, `requestedCount`, `loadedBarCount`, `matchedBarCount`, `earliestAvailableTimestamp`, `filteredByDate`, `truncatedByLoadCap`, `droppedLeadingBar`, `referenceDate`, `lastBarPeriodEnd`, `lastBarPartial`, `droppedPartialBar`, `candles` (array of `{timestamp, open, high, low, close, volume}`).
- **Error Codes**: `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`.

---

### 6.4 `sa_get_ticker_metadata`
Returns locally stored metadata (company names, region, sector, industry, currency, fundamentals, the user's own entries, and newest bar date). Never fetches.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbols` | `string[]` | Yes | - | MinItems: `1`, MaxItems: `MaxSymbolsPerMetadataCall` | - | Array of ticker symbols. |

- **Boundary Conditions**: Duplicate symbols are returned once after canonicalization. Missing symbols return records with `found: false`.
- **Return Fields**: `requestedCount`, `uniqueCount`, `results` (array of `{symbol, found, timeSeriesLastUpdated, metadata}`).
- **Error Codes**: `LIMIT_EXCEEDED`.

---

### 6.5 `sa_get_capabilities`
Returns the technical indicator catalog, parameter definitions, output series, supported timeframes, screener capabilities and (with `--enable-execution`) the execution block.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `category` | `string` | No | `null` | - | `Trend`, `Oscillator`, `Volume`, `Volatility`, `Math`, `Chart`, `Other` | Only indicators of this category. |
| `indicatorTypeFilter` | `string[]` | No | `null` | - | - | Only these indicator types (case-insensitive names, e.g. `RSI`); turns the details on unless `includeDetails` is `false`. |
| `includeDetails` | `boolean` | No | `true` with `indicatorTypeFilter`, else `false` | - | - | Adds parameters, output series and flags to each indicator. |
| `includeScreenerCatalog` | `boolean` | No | `false` | - | - | Adds the screener catalogue (columns, criteria, price sources, operators). |

- **Boundary Conditions**: An unknown name in `indicatorTypeFilter` raises `UNKNOWN_INDICATOR`. The indicator count comes from the runtime registry.
- **Return Fields**: `indicatorCount`, `periodUnit` (`"bars"`), `supportedTimeFrames`, `indicators` (array of `{type, isRegistered, displayName, category, backtestEligibility, isCrossTicker, parameters[{name, displayName, description, type, default, min, max, guiMin, guiMax}], outputSeriesNames, nonCausalSeries}`), `unitUndefined`, optional `screener`, `execution` (reports `enabled`; the limits and fixed values only with `--enable-execution`).
- **Error Codes**: `UNKNOWN_INDICATOR`.

---

### 6.6 `sa_get_settings`
Returns allow-listed operational settings shaping tool boundaries (`Tools/McpSettingsExposure.cs`).

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `section` | `string` | No | `null` | - | `chart`, `screener`, `backtest`, `marketStructure`, `prediction` | Only this section. |

- **Boundary Conditions**: Omission of `section` returns all allow-listed sections. File paths, credentials, colours and Python plumbing are never returned.
- **Return Fields**: `settings` (object: section name to an object of setting name and value).
- **Error Codes**: none specific.

---

### 6.7 `sa_calculate_indicators`
Calculates up to `MaxIndicatorsPerCall` indicators on stored bars using the core indicator pipeline. Supports indicator chaining and cross-ticker comparisons (at most `MaxSecondarySymbolsPerCall` comparison symbols).

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol. |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Bar duration. |
| `indicators` | `object[]` | Yes | - | MinItems: `1`, MaxItems: `MaxIndicatorsPerCall` | - | Array of indicator specification objects. |
| `indicators[].id` | `string` | Yes | - | unique within the call | - | Client-assigned identifier. |
| `indicators[].type` | `string` | Yes | - | - | - | Indicator type name from `sa_get_capabilities`. |
| `indicators[].parameters` | `object` | No | `{}` | - | - | Key-value dictionary of indicator parameters (a two-ticker indicator needs `ComparisonSymbol`). |
| `indicators[].priceSource` | `string` | No | `null` | - | `Close`, `Open`, `High`, `Low`, etc. | Price series input. |
| `indicators[].sourceIndicatorId` | `string` | No | `null` | - | - | ID of upstream indicator feeding input series. |
| `indicators[].sourceOutputSeriesName` | `string` | No | `null` | - | - | Series name of upstream indicator. |
| `indicators[].dynamicPeriodIndicatorId` | `string` | No | `null` | - | - | ID of indicator feeding adaptive period. |
| `indicators[].dynamicPeriodOutputSeriesName` | `string` | No | `null` | - | - | Series name of dynamic period indicator. |
| `lookbackBars` | `integer` | No | `MaxCandlesPerRequest` | Min: `1`, Max: `MaxCandlesPerRequest` | - | Stored bars the calculation runs over (warm-up window). |
| `outputTailCount` | `integer` | No | `DefaultOutputTailBars` | Min: `1`, Max: `MaxOutputTailBars` | - | Trailing bars returned per series. |
| `endDate` | `string` | No | latest stored bar | `yyyy-MM-dd` | - | Last date to include: the latest bar is the last bar dated on or before it, the window is the latest `lookbackBars` bars up to it, comparison symbols are cut at the same date. A malformed date is `INVALID_ARGUMENT` at `endDate`; no bar on or before it is `INSUFFICIENT_MARKET_DATA` at `endDate`. |
| `allowUnregistered` | `boolean` | No | `false` | - | - | Accepts `symbol` and `ComparisonSymbol` values that are not in the ticker list but have stored daily bars; they are named in `warnings`. A symbol with no stored daily bar is `SYMBOL_NOT_FOUND`. |

- **Boundary Conditions**: Leading nulls mean warm-up (`leadingNullCount`); if every value is null the result reports why. A failing indicator is reported in its own result and does not fail the call. Projected values (e.g. Ichimoku spans) belong to no bar yet.
- **Return Fields**: `symbol`, `timeFrame`, `endDate?` (only when given), `periodUnit`, `timestampFormat`, `lookbackBars`, `calculatedBarCount`, `returnedBarCount`, `earliestCalculatedTimestamp`, `latestTimestamp`, `droppedLeadingBar`, `timestamps`, `results` (keyed by indicator ID: `type`, `displayName`, `isSuccessful`, `errorMessage?`, `priceSource`, `series`, `latestValues`, `leadingNullCount`, `projectedValues?`, `nonCausalSeries?`), `warnings`.
- **Error Codes**: `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `UNKNOWN_INDICATOR`, `INVALID_INDICATOR_PARAMETER`, `INVALID_OUTPUT_SERIES`, `LIMIT_EXCEEDED`, `INSUFFICIENT_MARKET_DATA`.

---

### 6.8 `sa_run_screener`
Evaluates a condition tree against the latest bar of a symbol universe, with the Screener tab's own evaluation (daily bars by default; `W1` and `MN1` are built from the daily bars the way the Screener tab builds the bars of a side that names Week or Month; each side may name its own `timeFrame`; the tab has no `asOfDate`, `sortBy` or comparison-symbol bars). With `asOfDate` each symbol is evaluated on its last bar on or before that date; with `sortBy` the matches are ranked.

- **Heavy**: `true` (`HeavyToolTimeoutSeconds`)
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `conditionTree` | `object` | No | `null` | - | - | Condition tree: a group `{operator: And\|Or, children}` or one comparison `{left, comparison, right}`. Exactly one of `conditionTree` and `templateId`. |
| `templateId` | `string` | No | `null` | - | - | Saved screener template id from `sa_list_templates`. |
| `symbols` | `string[]` | No | `null` | MinItems: `1`, MaxItems: `MaxScreenerSymbols` | - | Specific universe. Mutually exclusive with `groupId`. |
| `groupId` | `string` | No | `null` | - | - | Workspace group GUID. Mutually exclusive with `symbols`. |
| `timeFrame` | `string` | No | `D1` | - | `D1`, `W1`, `MN1` | Bar duration of the whole scan, or of every side that names no `timeFrame` once a side does (see Per-side time frame); a `W1` or `MN1` bar is built from the stored daily bars. Other frames are `UNSUPPORTED_TIMEFRAME`. |
| `completeBarsOnly` | `boolean` | No | `true` | - | - | `W1` and `MN1` only (ignored for `D1`). `true`: when the latest period has not ended, the previous complete period is evaluated; `false`: the partial period is evaluated and named in the result. |
| `asOfDate` | `string` | No | latest stored bar | `yyyy-MM-dd` | - | Evaluation date (inclusive). Applies to every path (`conditionTree`, `templateId`, `groupId`) and to the comparison symbols, `includeMatchedValues` and `sortBy`. With it a column that is not read from the bars is `INVALID_ARGUMENT` (see Boundary Conditions). |
| `sortBy` | `object` | No | `null` | - | - | Side `{indicator\|price\|column: name, parameters?, outputSeriesName?, offset?}` the matched symbols are ranked by, evaluated like a comparison side (a two-ticker indicator needs `ComparisonSymbol`). A `criteria` side and a text column are `INVALID_ARGUMENT` (`sortBy.criteria`, `sortBy.column`). |
| `sortDirection` | `string` | No | `Descending` | - | `Descending`, `Ascending` | Needs `sortBy` (`INVALID_ARGUMENT` at `sortDirection` without it). |
| `includeMatchedValues` | `boolean` | No | `false` | - | - | Returns the values each comparison read, per returned symbol. |
| `maxReturnedMatches` | `integer` | No | `MaxReturnedMatches` | Min: `1`, Max: `MaxReturnedMatches` | - | Maximum matched symbols returned. |
| `allowUnregistered` | `boolean` | No | `false` | - | - | Accepts explicit `symbols` and `ComparisonSymbol` values that are not in the ticker list but have stored daily bars; they are named in `warnings`. Not for `groupId`. |

- **Boundary Conditions**: Omitting both `symbols` and `groupId` screens every available symbol. A comparison that cannot be calculated does not match. Look-ahead indicators are refused. Two-ticker indicators (`Correlation`, `CrossCorrelation`) need `ComparisonSymbol` (an empty one is refused: the chart's Price-vs-Volume mode is not available here).
  - **Two-ticker indicators on `W1`/`MN1`**: both symbols are read through the same bar selection (date cut, aggregation, partial-period rule, each symbol with its own reference date). A `W1` or `MN1` bar is stamped with the first stored day of its period and the two symbols are matched by that exact date, as on the chart: a period whose first stored day differs between the symbols has no value, a window that contains it has no value, the condition does not match and `warnings` stays silent; `sortBy` then gives `sortValue` `null`. A comparison symbol stored only to a mid-week day loses that week as partial (with `completeBarsOnly: true`) while the screened symbol keeps it.
  - **Per-side time frame**: a side object (`indicator`, `price`, `column`, `criteria`, and `sortBy`) takes an optional `timeFrame` (`D1`, `W1`, `MN1`; periods and `offset` count bars of that frame), so one condition can compare, for example, the daily `Close` with a weekly `SMA`: `{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":10},"timeFrame":"W1"}}`. While no side (and no saved template) names a frame, the whole scan is on the tool-level `timeFrame` as before. As soon as one does, every side reads its own frame: a side without `timeFrame` reads the tool-level `timeFrame` (default `D1`); a saved template that stores a `W1` or `MN1` bar-reading side reads its stored frames and refuses a tool-level `timeFrame` other than `D1` (`INVALID_ARGUMENT` at `timeFrame`). `INVALID_ARGUMENT` at `<side>.timeFrame`: a value that is not a frame the server supports, any `timeFrame` on a column not read from the bars (only `Open`, `High`, `Low`, `Close`, `Volume`, `Change`, `Change %` have bars), a frame other than `D1` on `AnchoredVWAP` or on a two-ticker indicator. A two-ticker indicator is refused anywhere in such a call (that scan reads no comparison symbol); in a saved template it is listed in `warnings` and never matches. The result then has `sideFrames` (below) instead of the single `partialLastBar`, `partialPeriodStarts` and `skippedPartialBarCount`. `sa_get_capabilities` with `includeScreenerCatalog` lists the frames in `screener.sideTimeFrames`.
  - **As of a date**: a symbol with no bar on or before `asOfDate` is not evaluated and is counted in `noDataAtAsOfCount`, not in `evaluatedSymbolCount`. Only the columns read from the bars (`Open`, `High`, `Low`, `Close`, `Volume`, `Change`, `Change %`) are accepted; every other column (also the text columns) is today's stored snapshot and is refused at its side path (`conditionTree.<comparison>.left.column`, `entries[i].leftHand`, `sortBy.column`) with the allowed names in the hint. A `groupId` adds a warning that its members are those of today. `asOfDate` removes look-ahead only, not survivorship bias (today's ticker list). An as-of scan reads the whole daily history of every scanned symbol.
  - **Partial `W1`/`MN1` period**: a period is partial while its last calendar day (the Sunday of the week, the last day of the month) is after the reference date, which is `asOfDate` or the last stored daily bar, whichever is earlier (the rule of `lastBarPartial` in `sa_get_candles`); a week whose last stored day is a Friday is partial. `completeBarsOnly: true` evaluates the previous period instead (`skippedPartialBarCount` counts the symbols), `false` evaluates the partial one (`partialLastBar: true`, `partialPeriodStarts`). Periods in conditions are bars of the requested frame.
  - **Ranking**: `Descending` puts the larger `sortValue` first, `Ascending` the smaller; a symbol without a value is last in both directions; equal values are in ascending symbol order. `matchedSymbols` then follow the ranking, `maxReturnedMatches` keeps the first ranks and `matchedSymbolCount` stays the true count. Without `sortBy` the list is ascending and there is no `ranking`. `sortValue` has the unit of the chosen side (indicator outputs have no stored unit). The sort side is read for every matched symbol.
- **Return Fields**: `timeFrame`, `asOfDate?` (only when given), `completeBarsOnly?` (`W1`/`MN1`), `universe`, `groupId`, `template{id, name}?`, `evaluatedSymbolCount`, `noDataAtAsOfCount?` (only with `asOfDate`), `partialLastBar?`, `partialPeriodStarts?` (only when true) and `skippedPartialBarCount?` (only with `completeBarsOnly` true) (`W1`/`MN1`; none of the three on a scan with per-side frames), `sideFrames?[{timeFrame, partialLastBar, partialPeriodStarts?, skippedPartialBarCount?, symbolsWithoutBarsCount, symbolsWithoutBars}]` (only on a scan with per-side frames, one entry per `W1`/`MN1` frame the condition read; `symbolsWithoutBars` lists the first `maxReturnedMatches`), `matchedSymbolCount`, `returnedMatchCount`, `truncated`, `treeNodeCount`, `treeDepth`, `matchedSymbols` (ascending; in ranking order with `sortBy`), `ranking?[{rank, symbol, sortValue}]` (only with `sortBy`; `sortValue` is a number or `null`), `comparisons[{id, path, text}]` and `matchedValues` (with `includeMatchedValues`), `warnings?`.
- **Error Codes**: `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `TEMPLATE_NOT_FOUND`, `GROUP_NOT_FOUND`, `CONDITION_LIMIT_EXCEEDED`, `LIMIT_EXCEEDED`.

---

### 6.9 `sa_list_templates`
Lists saved screener and backtest condition templates (favourites first, then by name). Reads the template folders without creating or migrating anything.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `templateType` | `string` | No | `screener` | - | `screener`, `backtestCondition` | Category of templates to retrieve. |

- **Boundary Conditions**: Unreadable files, and templates that cannot be turned into a listing entry, are omitted and tallied in `unreadableFileCount`; the rest of the list is still returned.
- **Return Fields**: `templateType`, `templateCount`, `unreadableFileCount`, `templates` (screener: `{id, name, description, isFavorite, updatedAt, entryCount, enabledEntryCount, entries[{text, enabled, combinedWithNext}]}`; backtestCondition: `{id, name, description, isFavorite, updatedAt, roots[{root, comparisonCount, text}], riskManagement{stopLossRatio, takeProfitRatio}?}`), `warnings` (per template; names why a backtest-condition template cannot run through `sa_run_backtest`).
- **Error Codes**: none specific.

---

### 6.10 `sa_run_backtest`
Executes an in-memory backtest on daily, weekly or monthly bars using the Legacy execution model (the Backtest window's path). No equity curve is provided; reconstruct the realized flow from the `closedNet` of the trades.

- **Heavy**: `true` (`HeavyToolTimeoutSeconds`)
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Target ticker symbol. |
| `timeFrame` | `string` | No | `D1` | - | `D1`, `W1`, `MN1` | Time frame of the bars to simulate. |
| `conditions` | `object` | No | `null` | - | - | Up to six roots (`entryLong`, `entryShort`, `exitLong`, `exitShort`, `reverseLong`, `reverseShort`), at least one. Exactly one of `conditions` and `templateId`. |
| `templateId` | `string` | No | `null` | - | - | Saved backtest-condition template id (`sa_list_templates` with `templateType` `backtestCondition`). |
| `startDate` | `string` | No | stored history | `yyyy-MM-dd` | - | First evaluated day. |
| `endDate` | `string` | No | latest stored bar | `yyyy-MM-dd` | - | Last evaluated day. |
| `account` | `object` | No | factory defaults | - | - | `initialCapital`, `commissionFlat`, `commissionPerUnit`, `slippageRatio`, `sizingModel` (`FixedQuantity`\|`PercentOfEquity`), `sizingParameter`, `initialMarginRatio`, `maintenanceMarginRatio`, `liquidationPenaltyRatio`. |
| `riskManagement` | `object` | No | `null` | - | - | `{stopLossRatio, takeProfitRatio}`; not with `templateId`. |
| `report` | `object` | No | factory defaults | - | - | `annualRiskFreeRate`, `annualMar`, `annualPeriodsD1` (1..366), `bootstrapSeed`, `bootstrapIterations` (at least 1000). |
| `maxReturnedTrades` | `integer` | No | `MaxReturnedTrades` | Min: `0`, Max: `MaxReturnedTrades` | - | Max trades listed; `0` lists none. |
| `allowUnregistered` | `boolean` | No | `false` | - | - | Accepts a `symbol` that is not in the ticker list but has stored bars; it is named in `warnings`. A symbol with no stored bars is `SYMBOL_NOT_FOUND`. |

- **Boundary Conditions**: With `templateId`, `riskManagement` must be omitted (the template's risk settings govern). `startDate > endDate` raises `INVALID_ARGUMENT`. A side whose time frame differs from the evaluated `timeFrame` is `UNSUPPORTED_TIMEFRAME`. Two-ticker indicators are refused. Omitted settings are the window's factory defaults and every effective value is echoed.
- **Return Fields**: `symbol`, `timeFrame`, `executionModel`, `status`, `isInsufficientData`, `strategy`, `conditionSource`, `template{id, name}?`, `period{startDate, endDate, datesFromStoredHistory, warmUpBarCount, evaluatedBarCount}`, `lastProcessedBar`, `equity{initial, final}`, `reproducibilityHash`, `runFingerprint`, `report` (metrics keep status, value, unit and reason), `effectiveSettings{account, riskManagement, report}`, `conditionRoots`, `trades{totalTrades, returnedTrades, truncated, order, items}`, `engineWarnings`, `warnings`.
- **Error Codes**: `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `TEMPLATE_NOT_FOUND`, `INVALID_INDICATOR_PARAMETER`, `CONDITION_LIMIT_EXCEEDED`, `INSUFFICIENT_MARKET_DATA`, `LIMIT_EXCEEDED`.

---

### 6.11 `sa_calculate_reverse_watch`
Calculates the Reverse Watch price-volume curve the chart draws: the phase (`Phase1`..`Phase8`, or `Phase0` for flat data) and window position of each point. No meaning is attached to a phase.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol. |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Bar duration. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Last date to include. |
| `period` | `integer` | No | `25` | Min: `1`, Max: `MaxCandlesPerRequest` | - | Moving-average window in bars. |
| `isMaBased` | `boolean` | No | `true` | - | - | `true`: averages price and volume; `false`: close and volume of each bar. |
| `isLogScaleVolume` | `boolean` | No | `false` | - | - | `true`: applies log10 to volume. |
| `dataCount` | `integer` | No | `100` | Min: `2`, Max: `MaxCandlesPerRequest` | - | Latest points the window's centre and bounds are taken from. |
| `lookbackBars` | `integer` | No | `MaxCandlesPerRequest` | Min: `1`, Max: `MaxCandlesPerRequest` | - | Stored bars the calculation runs over; at least `period + dataCount`. |
| `outputTailCount` | `integer` | No | `DefaultOutputTailBars` | Min: `1`, Max: `MaxOutputTailBars` | - | Trailing points returned. |

- **Boundary Conditions**: Fewer stored bars than `period` (or none) is `INSUFFICIENT_MARKET_DATA` naming `period`. `dataCount < 2` or `lookbackBars < period + dataCount` is `INVALID_ARGUMENT`. Zero variation across the window gives `Phase0` with neutral positions.
- **Return Fields**: `symbol`, `timeFrame`, `endDate?`, `calculatedBarCount`, `pointCount`, `period`, `isMaBased`, `isLogScaleVolume`, `dataCount`, `bounds{minPrice, maxPrice, rawMinPrice, rawMaxPrice, centerPrice, minVolume, maxVolume, rawMinVolume, rawMaxVolume, centerVolume, volumeUnit}`, `recent` (array of `{date, phase, pricePosition, volumePosition, priceAverage, volumeAverage, close, volume, angle?, phaseNumber?, phaseDescription?}`, oldest first).
- **Error Codes**: `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `INSUFFICIENT_MARKET_DATA`, `LIMIT_EXCEEDED`.

---

### 6.12 `sa_calculate_chart_series`
Computes the converted numerical series, effective sizing, DataWindow metrics and optional pattern analysis of Heikin-Ashi and the non-time-series charts (Renko, Kagi, Point and Figure, Three Line Break), with the Core utilities the GUI uses.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol. |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Base candle timeframe. |
| `chartType` | `string` | Yes | - | - | `HeikinAshi`, `Renko`, `Kagi`, `PointAndFigure`, `ThreeLineBreak` | Target chart type. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Last date to include. |
| `lookbackBars` | `integer` | No | `MaxCandlesPerRequest` | Min: `1`, Max: `MaxCandlesPerRequest` | - | Stored bars the calculation runs over. |
| `outputTailCount` | `integer` | No | `DefaultOutputTailBars` | Min: `1`, Max: `MaxOutputTailBars` | - | Trailing converted elements returned. |
| `sizingMode` | `string` | No | `AutoAtr` | - | `Fixed`, `AutoAtr`, `Percentage` | Sizing mode for Renko, Kagi, P&F. |
| `reversalMode` | `string` | No | `AutoAtr` | - | `Fixed`, `AutoAtr`, `Percentage` | Alias for `sizingMode` on Kagi. |
| `manualSize` | `number` | No | `1.0` | `> 0` | - | Size when `sizingMode` is `Fixed` or fallback `Fixed`. Aliases: `brickSize` (Renko), `boxSize` (P&F), `reversalAmount` (Kagi). |
| `percentage` | `number` | No | `1.0` | `0.01..100` | - | Size when `sizingMode` is `Percentage` or fallback `Percentage`. Aliases: `brickPercent`, `boxPercent`, `reversalPercent`. |
| `atrPeriod` | `integer` | No | `14` | Min: `1`, Max: `500` | - | ATR period for `AutoAtr`. |
| `atrMultiplier` | `number` | No | `1.0` | `> 0` | - | ATR multiplier for `AutoAtr`. |
| `roundingMode` | `string` | No | `None` | - | `None`, `NiceNumbers`, `TickSize`, `Floor`, `Ceiling`, `Round` | Rounding of the size. |
| `fallbackMode` | `string` | No | `Percentage` | - | `Percentage`, `Fixed` | Used when ATR is 0 or insufficient. |
| `reversal` | `integer` | No | Renko `2`, P&F `3` | Min: `1`, Max: `10` | - | Reversal requirement in bricks/boxes. |
| `initialColumn` | `integer` | No | `0` | Min: `0`, Max: `10000` | - | Kagi initial column offset. |
| `lineCount` | `integer` | No | `3` | Min: `1`, Max: `20` | - | Three Line Break line count. |
| `minimumMove` | `number` | No | `0` | `>= 0` | - | Three Line Break minimum movement. |
| `tolerance` | `number` | No | `0.0001` | `>= 0` | - | Three Line Break trend tolerance. |
| `includeMultiWave` | `boolean` | No | `false` | - | - | Multi-Wave pattern analysis on converted elements. |
| `includePatterns` | `boolean` | No | `false` | - | - | Point and Figure pattern analysis (P&F only). |
| `showDoubleBreakout`, `showTripleBreakout`, `showTrendlineBreakout`, `showTriangleBreakout`, `showCatapultBreakout` | `boolean` | No | `true` | - | - | Which P&F breakout signals the pattern analysis includes. |

- **Boundary Conditions**: Non-positive sizes, a percentage outside `0.01..100` and negative `minimumMove` or `tolerance` raise `INVALID_ARGUMENT`.
- **Return Fields**: `symbol`, `timeFrame`, `chartType`, `endDate?`, `sourceBarCount`, `elementCount`, `outputStartElementIndex`, `effectiveSize?`, `totalColumns?`, `elements` (array of `{index, timestamp, open, high, low, close, volume, direction, columnIndex?, columnType?, isYang?, lineState?, columnSpan?, runCount?, boxCount?, reversalPrice?, nextTargetPrice?, startDate?, endDate?, reversalDate?, updateDate?, trendDays?}`), `multiWaveSignals?`, `pnfAnalysis?`.
- **Error Codes**: `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `INSUFFICIENT_MARKET_DATA`, `LIMIT_EXCEEDED`.

---

### 6.13 `sa_check_portfolio_rl_preflight`
Performs read-only feasibility and readiness checks across 2 to 100 tickers for portfolio reinforcement learning, without modifying Data or executing training. The stored symbols are read from the configured `MarketDataSettings` daily and metadata folders. The universe size, holdout share and partition minimums come from the `RlPortfolio` section of `appsettings.json` (the one the GUI reads); the server checks it at start-up: a value outside the hard band stops the start (invalid configuration), a value outside the standard band is logged to standard error.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbols` | `string[]` | Yes | - | MinItems: `2`, MaxItems: `100` | - | Portfolio universe tickers. |
| `startDate` | `string` | No | proposed | `yyyy-MM-dd` | - | Candidate start date. |
| `endDateExclusive` | `string` | No | proposed | `yyyy-MM-dd` | - | First date outside the period. |
| `symbolHoldout` | `boolean` | No | `true` | - | - | Whether symbol holdout is requested. |
| `mode` | `string` | No | `Standard` | - | `Standard`, `ShortHistory` | Evaluation mode. |
| `maxAssetWeightPercent` | `integer` | No | `100` | Min: `1`, Max: `100` | - | Maximum portfolio asset weight cap. |

- **Boundary Conditions**: Fewer than 2 symbols raises `INVALID_ARGUMENT`. Symbols that are not ready are reported in the result (`excludedSymbols`, `symbolDetails`), not as an error.
- **Return Fields**: `canStart`, `blocker`, `feasible`, `symbolHoldout`, `calendarLength`, `readyCount`, `startDate`, `endDateExclusive`, `trainingSymbols`, `excludedSymbols`, `symbolDetails` (array of `{symbol, status, role, barsInPeriod}`).
- **Error Codes**: `SYMBOL_NOT_FOUND`.

---

### 6.14 `sa_check_rl_playback_preflight`
Checks whether a registered reinforcement learning model can be replayed on a symbol's stored daily bars, and reports the valid anchor date range. Never writes the Data tree or the model registry; it may create and delete a transient working folder under the operating system's temporary folder while it reads the stored bars.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `modelId` | `string` | Yes | - | - | - | Registered RL model generation id, or `"active"`. |
| `symbol` | `string` | No | model's trained symbol | - | - | Ticker symbol (`.` and `-` interchangeable). |

- **Return Fields**: `canReplay`, `modelId`, `symbol`, `trainedSymbol`, `targetPolicy`, `episodeHorizon` (128), `observationWindow` (64), `earliestAnchorDate?`, `latestAnchorDate?`, `reason?` (why `canReplay` is `false`).
- **Error Codes**: `SYMBOL_NOT_FOUND`.

---

### 6.15 `sa_calculate_relative_performance`
Calculates the values the Relative Performance chart plots for a primary symbol and its comparison symbols, aligned to the primary symbol's bars by the chart's own aligner. Stateless: the chart's saved symbol list, mode, period and presets are not read.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Primary ticker symbol (the timeline of the alignment). |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Bar duration. |
| `comparisonSymbols` | `string[]` | No | `[]` | At most `MaxComparisonTargets` different symbols | - | Stored comparison symbols. The primary symbol and repeats are ignored and listed in `ignoredSymbols`. |
| `mode` | `string` | No | `Performance` | - | `Performance`, `Ratio`, `ZScore`, `Spread` | Comparison mode (exact spelling). |
| `zScorePeriod` | `integer` | No | `20` | Min: `2`, Max: `500` | - | Z-Score window in bars; checked in every mode, used by `ZScore` only. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Last date to include; the primary and every comparison symbol are cut at that day. |
| `lookbackBars` | `integer` | No | `MaxCandlesPerRequest` | Min: `1`, Max: `MaxCandlesPerRequest` | - | Primary bars the alignment runs over. |
| `outputTailCount` | `integer` | No | `DefaultOutputTailBars` | Min: `1`, Max: `MaxOutputTailBars` | - | Trailing bars returned; the first of them is the Performance base. |

- **Modes** (`unit` in the result): `Performance` = (close − base) / base × 100, `percent`; `Ratio` = close / primary close, `ratio`; `Spread` = close − primary close, `price-difference`; `ZScore` = (close − mean) / population standard deviation over the last `zScorePeriod` bars, `sigma`. Formulas: `indicators_formulas.md` (ids `indicators_formulas_0052`, `indicators_formulas_0535`, `indicators_formulas_0536`).
- **Boundary Conditions**: A value that cannot be defined is `null`. Gaps of a comparison symbol are carried forward (at most 30 bars); a symbol with more than 50 % missing bars is left out (`excludedSymbols` and `warnings`). `outputTailCount` larger than the aligned bars returns all of them. `zScorePeriod` of 1 is refused (no deviation of one bar), although the settings dialog accepts it. The ±3.5σ drawing clip of the chart is not applied.
- **Return Fields**: `symbol`, `timeFrame`, `endDate?`, `mode`, `unit`, `zScorePeriod?`, `lookbackBars`, `alignedBarCount`, `returnedBarCount`, `baselineDate?` (Performance), `timestamps` (array), `series` (object keyed by symbol, primary first: `isPrimary`, `baseDate?`, `basePrice?`, `values` with `null` for undefined), `excludedSymbols` (`{symbol, reason}`), `ignoredSymbols` (`{symbol, reason}`), `warnings`.
- **Error Codes**: `INVALID_ARGUMENT` (malformed symbol, `mode` spelling, `zScorePeriod` outside 2..500, malformed `endDate`), `SYMBOL_NOT_FOUND`, `INSUFFICIENT_MARKET_DATA`, `LIMIT_EXCEEDED`.
- **Order of checks for `comparisonSymbols`**: each element in turn has its shape checked (`INVALID_ARGUMENT` at `comparisonSymbols[i]`) and is counted; the first different symbol over the limit is `LIMIT_EXCEEDED` at once and the rest of the list is not examined; only a list that passed gets the stored-ticker lookups (`SYMBOL_NOT_FOUND`). A list over the limit is answered with `LIMIT_EXCEEDED` without any lookup, even if it also contains an unknown symbol.

---

### 6.16 `sa_filter_tickers`
Filters tickers with rules sent in the call (the Tickers-tab rule filter, without a saved filter or group), optionally followed by a Screener condition. The same rule evaluator and row calculation as a saved Tickers-tab filter.

- **Heavy**: `true` (`HeavyToolTimeoutSeconds`; the screener can read long histories of up to `MaxScreenerSymbols` symbols)
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbols` | `string[]` | No | `null` | MinItems: `1`, MaxItems: `MaxFilterEvaluationSymbols` | - | Symbols to filter. Mutually exclusive with `groupId`. Omit both to filter every registered symbol. |
| `groupId` | `string` | No | `null` | - | - | GUID of a saved workspace group from `sa_list_ticker_groups`; its members are the universe. |
| `rules` | `object[]` | No | `[]` | MaxItems: `MaxInlineFilterRules` | - | Rules, all must hold (AND). |
| `rules[].field` | `string` | Yes | - | - | Tickers-tab column name or alias | Unknown field is `INVALID_ARGUMENT`. |
| `rules[].operator` | `string` | Yes | - | - | per field kind, see below | Case-insensitive. |
| `rules[].value` | `string` | Yes | - | - | - | Number (numeric fields) or text; a field name when `isCompareToField` is true. |
| `rules[].isCompareToField` | `boolean` | No | `false` | - | - | `value` names another field of the same kind. |
| `screener` | `object` | No | `null` | - | - | `{conditionTree}`: the condition grammar of `sa_run_screener`, applied after the rules to the symbols they kept. `templateId` is not supported. |
| `includeFields` | `string[]` | No | `[]` | at most the number of known column names and aliases | known column names or aliases | Columns added to each returned match (`null` when not available). |
| `offset` | `integer` | No | `0` | Min: `0` | - | Matches to skip. |
| `limit` | `integer` | No | `100` | Min: `1`, Max: `MaxTickersPerPage` | - | Page size. |
| `allowUnregistered` | `boolean` | No | `false` | - | - | Evaluates listed `symbols` that are not in the ticker list but have stored daily bars (named in `warnings`). A listed symbol with no stored daily bar is `SYMBOL_NOT_FOUND`. Not for `groupId` or the whole-list universe. |

- **Operators by field kind**: numeric fields: `==`, `=`, `Equals`, `!=`, `NotEquals`, `>`, `GreaterThan`, `>=`, `GreaterThanOrEqual`, `<`, `LessThan`, `<=`, `LessThanOrEqual` (`value` must be a number). Text, flag and date-time fields (compared through their text, ignoring case): `*=`, `Contains`, `!*=`, `NotContains`, `!Contains`, `==`, `=`, `Equals`, `!=`, `NotEquals`. The `Tag` field: the text operators plus `ContainsTag` (tags are separated by comma or semicolon). With `isCompareToField`, numeric compares with numeric and text with text; `Tag` may be compared with any field.
- **Evaluation order**: universe, rules, screener (on the symbols the rules kept), sort (ordinal, ascending), paging.
- **Boundary Conditions**: Without rules and screener the whole universe is returned. An empty universe is an empty result, not an error. Symbols are canonicalised and de-duplicated; a symbol not in the ticker list is counted in `universe.unregisteredCount` and left out (one warning), unless `allowUnregistered` is true. A price-dependent field uses the latest stored daily bar and the one before it; a symbol without stored bars matches no such rule. A bad condition, or one that names a comparison symbol which is not stored, is refused whatever the rules keep. At most `MaxScreenerSymbols` symbols may reach the screener. There is no `compareMode`: the evaluator ignores it.
- **Units**: field values keep the unit of the Tickers-tab column (for example `ChangePercent` is a percent value); the tool converts nothing.
- **Return Fields**: `universe{kind (symbols|group|all), inputCount, unregisteredCount, groupId?, evaluatedCount}`, `ruleCount`, `screenerApplied`, `screener{evaluatedSymbolCount, treeNodeCount, treeDepth}?`, `totalMatchCount`, `offset`, `limit`, `returnedCount`, `hasMore`, `matches` (array of `{symbol, fields?}`), `warnings`.
- **Error Codes**: `INVALID_ARGUMENT` (path `rules[i].field`, `rules[i].operator`, `rules[i].value`, `includeFields[i]`, `symbols[i]`, `groupId`, `screener...`), `LIMIT_EXCEEDED` (`rules`, `symbols`, `groupId`, `limit`, `includeFields`, `screener.conditionTree`), `GROUP_NOT_FOUND`, `SYMBOL_NOT_FOUND`, `UNKNOWN_INDICATOR`, `INVALID_INDICATOR_PARAMETER`, `CONDITION_LIMIT_EXCEEDED`.

---

## 7. Execution Tools Reference (7 Tools, `--enable-execution` Required)

Opt-in execution tools operating exclusively within an ephemeral OS temporary sandbox directory (`<OS temp>/StockAnalyzer/ephemeral`); nothing is saved under `Data` and nothing is registered. Without the flag they are absent from `tools/list` and `tools/call` answers `-32602`. They are marked as not read-only in `tools/list`. Work runs in a supervised child process: the start tools return at once (`sa_start_portfolio_rl_training` is the only one marked heavy). A job started by any of the three start tools is polled with `sa_get_training_status` and stopped with `sa_cancel_training` (one job table, one `jobId` format).

### 7.1 `sa_start_training`
Starts one supervised training job (the Training Wizard's path) on stored daily bars.

- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbols` | `string[]` | Yes | - | 1..`MaxSymbolsPerMetadataCall`, none twice | - | Stored training symbols. |
| `startDate` | `string` | No | start of stored data | `yyyy-MM-dd` | - | First date of the training data. |
| `endDate` | `string` | No | end of stored data | `yyyy-MM-dd` | - | Last date of the training data; `startDate` after it is `INVALID_ARGUMENT`. |
| `framework` | `string` | Yes | - | - | `PyTorch`, `LightGBM`, `TensorFlow` | Training framework. |
| `architecture` | `string` | Yes | - | - | `execution.architectures` of `sa_get_capabilities` | Architecture of the framework. |
| `windowSize` | `integer` | Yes | - | - | - | Input length in bars. |
| `horizon` | `integer` | Yes | - | - | - | Bars ahead of the label. |
| `targetType` | `string` | No | `Classification` | - | `Classification`, `Regression` | Learning objective. |
| `nSplits` | `integer` | No | `5` | - | - | Walk-forward splits. |
| `fixedZScore` | `boolean` | No | `false` | - | - | Standardize features with population Z-score. |
| `clipSigma` | `number` | No | `null` | `> 0` | - | Outlier clipping multiple after Z-score. |
| `calibration` | `boolean` | No | `false` | - | - | Fitted temperature probability calibration (classification). |
| `cpcv` | `boolean` | No | `false` | - | - | Combinatorial purged cross-validation evaluation. |
| `hyperparameters` | `object` | No | `{}` | - | - | Trainer hyperparameters by key (`execution.hyperparameters`); a value is a string or a number. |

- **Return Fields**: `jobId`, `state`, `artifactsLocation` (the fixed text `os-temp`; no path).
- **Error Codes**: `SYMBOL_NOT_FOUND`, `LIMIT_EXCEEDED`, `EXECUTION_BUSY`, `PYTHON_ENV_MISSING`, `SANDBOX_PATH_TOO_LONG` (every rule of `TrainingJobConfig.Validate` and `TrainingResourceGuard` is `INVALID_ARGUMENT`).

---

### 7.2 `sa_get_training_status`
Returns the state of a job started with `sa_start_training`, `sa_start_rl_training` or `sa_start_portfolio_rl_training`.

- **Arguments**: `jobId` (`string`, required): the `jobId` returned by a start tool.
- **Return Fields**: `jobId`, `kind` (`supervised`\|`reinforcement_learning`\|`portfolio_reinforcement_learning`), `state` (`Running`, `Cancelling`, `Validating`, `Succeeded`, `Failed`, `Cancelled`), `stage`, `progressPercent` (0..100), `elapsedSeconds`, `metrics?` (supervised), `messages` (latest `StatusTailMessages` lines, path-free), `request` (the settings actually used), `artifact?` (supervised, when `Succeeded`: `{artifactId, onnxFileName, sizeBytes, sha256, validation{loads, inputShape, outputShape}}`), `result?` (RL kinds), `failureReason?` (`TIMEOUT`, `INVALID_ONNX`, `TRAINING_FAILED`, when `Failed`).
- **Error Codes**: `INVALID_ARGUMENT` (a `jobId` not shaped like one), `JOB_NOT_FOUND`.

---

### 7.3 `sa_cancel_training`
Stops a job of any kind: the whole process tree is stopped, waiting up to `CancelGraceSeconds`, and no model is left behind.

- **Arguments**: `jobId` (`string`, required): the job to cancel.
- **Return Fields**: `jobId`, `state` (`Cancelling` while the trainer is stopped, `Cancelled` once it is; a job that already ended keeps its final state).
- **Error Codes**: `INVALID_ARGUMENT`, `JOB_NOT_FOUND`.

---

### 7.4 `sa_start_rl_training`
Starts one reinforcement-learning smoke run (the trial run of the RL window: discrete policy, one seed, factory costs, no registrable model).

- **Arguments**:

| Argument | Type | Req | Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Stored symbol (an equity with stored metadata). |
| `startDate` | `string` | No | earliest start from which the required bars are stored | First date of the period. |
| `endDateExclusive` | `string` | No | the day after the latest final stored bar | First day not in the period; `startDate` not before it is `INVALID_ARGUMENT`. |

- **Boundary Conditions**: No other argument exists (an unknown one is `INVALID_ARGUMENT`); the costs and every training choice are fixed. The job never has an artifact, so `sa_run_onnx_prediction` answers `ARTIFACT_NOT_FOUND` for its `jobId`. The managed RL environment (`StockAnalyzer.Python/.venv` with `gymnasium` and `stable-baselines3`) is probed before a job exists.
- **Return Fields**: `jobId`, `state`, `artifactsLocation`.
- **Error Codes**: `SYMBOL_NOT_FOUND`, `INSUFFICIENT_MARKET_DATA`, `EXECUTION_BUSY`, `PYTHON_ENV_MISSING`, `SANDBOX_PATH_TOO_LONG`.

---

### 7.5 `sa_start_portfolio_rl_training`
Starts one sandboxed portfolio reinforcement-learning smoke run across 2 to 100 tickers.

- **Heavy**: `true`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbols` | `string[]` | Yes | - | MinItems: `2`, MaxItems: `100` | - | Portfolio universe tickers. |
| `startDate` | `string` | No | proposed from stored data | `yyyy-MM-dd` | - | First date of the period. |
| `endDateExclusive` | `string` | No | proposed from stored data | `yyyy-MM-dd` | - | First day not in the period. |
| `symbolHoldout` | `boolean` | No | `true` | - | - | Whether symbol holdout is requested. |
| `maxAssetWeightPercent` | `integer` | No | `100` | Min: `1`, Max: `100` | - | Maximum asset weight percent. |
| `mode` | `string` | No | `Standard` | - | `Standard`, `ShortHistory` | Evaluation mode. |

- **Boundary Conditions**: Smoke run only (1 seed, 1024 transitions, factory costs, evaluation only). `sa_get_training_status` reports `policyReturn` and `baselineComparison` (`cashReturn`, `equalBuyHoldReturn`, `equalDailyRebalancedReturn`).
- **Return Fields**: `jobId`, `state`, `artifactsLocation`.
- **Error Codes**: `SYMBOL_NOT_FOUND`, `INSUFFICIENT_MARKET_DATA`, `EXECUTION_BUSY`, `PYTHON_ENV_MISSING`, `SANDBOX_PATH_TOO_LONG`.

---

### 7.6 `sa_run_onnx_prediction`
Runs a model trained with `sa_start_training` or a registered production model on stored daily bars. Each prediction uses only the model's window of bars ending at its own bar. The numbers are model outputs for research, not advice.

- **Arguments**:

| Argument | Type | Req | Default | Bounds | Description |
|---|---|---|---|---|---|
| `artifactId` | `string` | One of the two | - | exclusive with `modelId` | `artifactId` of a succeeded job, or `latest` (the newest succeeded model). |
| `modelId` | `string` | One of the two | - | exclusive with `artifactId` | Registered model generation id, or `active`. |
| `symbol` | `string` | Yes | - | - | Stored symbol. |
| `asOfDate` | `string` | No | latest stored daily bar | `yyyy-MM-dd` | Last bar to predict. |
| `count` | `integer` | No | `1` | Min: `1`, Max: `MaxPredictionBars` (default 64) | Consecutive bars, oldest first. |

- **Return Fields**: `artifactId` or `modelId`, `symbol`, `timeFrame` (`D1`), `asOfDate`, `modelContract{windowSize, featureMode, targetType, classCount, horizonBars, sha256}`, `predictions` (classifier: `{date, classScores[{class, probability}], predictedClass, confidence}`; regressor: `{date, value}`, a predicted log return).
- **Error Codes**: `SYMBOL_NOT_FOUND`, `ARTIFACT_NOT_FOUND`, `INSUFFICIENT_MARKET_DATA` (fewer than `windowSize + count - 1` bars up to `asOfDate`), `LIMIT_EXCEEDED` (`windowSize + count - 1` exceeds `MaxCandlesPerRequest`; the hint names the largest `count`).

---

### 7.7 `sa_get_rl_playback`
Replays a registered reinforcement-learning model over stored daily bars in an isolated workspace and evaluates the discrete action values.

- **Arguments**:

| Argument | Type | Req | Default | Bounds | Description |
|---|---|---|---|---|---|
| `modelId` | `string` | Yes | - | - | Registered RL model generation id, or `active`. |
| `symbol` | `string` | No | model's trained symbol | - | Stored symbol. |
| `asOfDate` | `string` | No | latest valid anchor date | `yyyy-MM-dd` | Anchor date of the observation window. |
| `decisionPosition` | `integer` | No | `0` | Min: `0`, Max: `127` | Decision step within the episode horizon. |
| `policyId` | `string` | No | model's target policy | - | Evaluation policy id. |

- **Return Fields**: `modelId`, `symbol`, `anchorDate`, `decisionPosition`, `qValues{hold, buy, sell}`, `selectedAction`, `discountFactor` (0.99), `policyId`, `hasPriceScaleWarning`, `hasDataGapWarning`.
- **Error Codes**: `ARTIFACT_NOT_FOUND`, `INSUFFICIENT_MARKET_DATA`.
