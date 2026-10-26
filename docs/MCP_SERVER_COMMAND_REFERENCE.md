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
|                     - catalog/indicators                              - 14 Read-Only Tools|
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
| `MaxCandlesPerRequest` | `int` | `10000` | `500` | `50000` | Maximum number of OHLCV bars returned or processed in a single tool call. |
| `DefaultCandleCount` | `int` | `200` | `10` | `2000` | Default bar count when `count` argument is omitted in `sa_get_candles`. |
| `MaxIndicatorsPerCall` | `int` | `16` | `1` | `64` | Maximum number of distinct indicator instances calculated in a single `sa_calculate_indicators` call. |
| `MaxSymbolsPerMetadataCall` | `int` | `100` | `1` | `500` | Maximum number of ticker symbols accepted in a single `sa_get_ticker_metadata` call. |
| `MaxReturnedTrades` | `int` | `200` | `10` | `2000` | Maximum number of closed trades returned in `sa_run_backtest`. |
| `MaxReturnedWarnings` | `int` | `20` | `5` | `100` | Maximum number of execution warning messages returned in `sa_run_backtest`. |
| `MaxOutputTailBars` | `int` | `500` | `10` | `5000` | Maximum number of trailing calculated bars returned in indicator or chart series calls. |
| `DefaultOutputTailBars` | `int` | `50` | `5` | `500` | Default trailing bar count when `outputTailCount` is omitted. |
| `ToolTimeoutSeconds` | `int` | `30` | `5` | `300` | Cancellation timeout for standard read-only tools. |
| `HeavyToolTimeoutSeconds` | `int` | `60` | `10` | `600` | Cancellation timeout for heavy computational tools (`sa_run_screener`, `sa_run_backtest`). |
| `QueueWaitSeconds` | `int` | `30` | `5` | `120` | Maximum time a request waits in the internal execution queue before timeout. |
| `MaxResponseBytes` | `int` | `4194304` | `262144` | `33554432` | Maximum byte size of a serialized JSON-RPC response payload (default: 4 MB). |
| `MaxRequestBytes` | `int` | `1048576` | `65536` | `8388608` | Maximum byte size of an incoming JSON-RPC request line (default: 1 MB). |
| `MaxScreenerSymbols` | `int` | `500` | `10` | `5000` | Maximum number of symbols evaluated during an explicit universe screener run. |
| `MaxReturnedMatches` | `int` | `200` | `10` | `2000` | Maximum number of matched symbols returned by `sa_run_screener`. |
| `MaxSecondarySymbolsPerCall` | `int` | `4` | `1` | `16` | Maximum number of distinct comparison symbols allowed for cross-ticker indicators. |
| `DefaultTickerPageSize` | `int` | `100` | `10` | `500` | Default page size when `limit` is omitted in `sa_list_tickers`. |
| `MaxTickersPerPage` | `int` | `1000` | `50` | `5000` | Maximum page size allowed in `sa_list_tickers`. |
| `MaxGroupNodes` | `int` | `500` | `50` | `5000` | Maximum number of group nodes returned by `sa_list_ticker_groups`. |
| `MaxFilterEvaluationSymbols` | `int` | `1000` | `100` | `10000` | Maximum number of symbols evaluated when filtering ticker lists. |

---

## 4. MCP Resources Specification

URI Scheme: `stockanalyzer://`

| Resource URI | MIME Type | Name | Content Description |
|---|---|---|---|
| `stockanalyzer://catalog/indicators` | `application/json` | Indicator Catalogue | Complete JSON catalog of all indicator types, parameter definitions, bounds, output series, and causality flags. |
| `stockanalyzer://catalog/screener` | `application/json` | Screener Catalogue | Catalog of supported screener columns, criteria patterns, price sources, operators, and unavailable column definitions. |
| `stockanalyzer://catalog/settings/current` | `application/json` | Current Settings | Allow-listed operational settings (limits and defaults). File paths and credentials are strictly excluded. |

---

## 5. Error Handling & Error Codes

### 5.1 JSON-RPC Protocol Faults vs. Tool Execution Failures
- **JSON-RPC Errors**: Reserved for transport, framing, or JSON parsing failures (`ParseError: -32700`, `InvalidRequest: -32600`, `MethodNotFound: -32601`, `InvalidParams: -32602`, `InternalError: -32603`).
- **Tool Failures**: Domain, logic, or calculation errors return a valid JSON-RPC result containing `isError: true` and an error JSON payload conforming to `{ "errorCode": string, "message": string, "argumentName": string?, "remedy": string? }`.

### 5.2 Tool Error Codes (`ToolErrorCodes`)

| Error Code | Scope | Description |
|---|---|---|
| `INVALID_ARGUMENT` | All Tools | Argument syntax violation, unknown argument, out-of-range value, or logical contradiction (e.g., `startDate > endDate`). |
| `SYMBOL_NOT_FOUND` | Symbol Tools | Requested ticker symbol is not registered or cannot be resolved. |
| `UNSUPPORTED_TIMEFRAME` | Timeframe Tools | Requested timeframe is not supported by the invoked tool (e.g., requesting `W1` on `sa_run_screener`). |
| `UNKNOWN_INDICATOR` | Indicator Tools | Requested indicator type name does not exist in `IndicatorCatalog`. |
| `INVALID_INDICATOR_PARAMETER` | Indicator Tools | Specified parameter name is invalid or value is outside `CoreParameterRange`. |
| `INVALID_OUTPUT_SERIES` | Indicator Tools | Requested output series name does not exist on the specified indicator. |
| `CONDITION_LIMIT_EXCEEDED` | Screener / Backtest | Condition tree depth, node count, or offset exceeds configured limits. |
| `LIMIT_EXCEEDED` | Batch Tools | Requested batch size (symbols, items) exceeds `McpLimits` allowance. |
| `INSUFFICIENT_MARKET_DATA` | Calculation Tools | Stored candle history has fewer bars than required for indicator warm-up. |
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

---

## 6. Read-Only Tools Reference (14 Tools)

### 6.1 `sa_list_tickers`
Lists registered ticker symbols (All Tickers), ordinally sorted. Canonical format: uppercase with `-` (e.g., `7203-T`).

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `offset` | `integer` | No | `0` | Min: `0` | - | Number of matching tickers to skip. |
| `limit` | `integer` | No | `100` | Min: `1`, Max: `1000` | - | Page size. |
| `searchQuery` | `string` | No | `null` | - | - | Case-insensitive substring match against symbol or short name. |
| `includeMetadataSummary` | `boolean` | No | `false` | - | - | Inlines company identity fields. Limited to at most `MaxSymbolsPerMetadataCall` (100). |
| `groupId` | `string` | No | `null` | - | - | GUID of a saved workspace group from `sa_list_ticker_groups`. |

- **Boundary Conditions**: If `offset` exceeds total matching symbols, an empty array is returned with `hasMore: false`.
- **Return Fields**: `tickers` (array of objects), `totalCount` (`int`), `offset` (`int`), `limit` (`int`), `hasMore` (`bool`).
- **Error Codes**: `INVALID_ARGUMENT`, `GROUP_NOT_FOUND`, `LIMIT_EXCEEDED`.

---

### 6.2 `sa_list_ticker_groups`
Lists workspace ticker groups (Watchlists, Portfolios, Custom Categories, List Folders, and Tickers-tab filters).

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `includeRules` | `boolean` | No | `false` | - | - | Inlines configured filter rule tree for filter nodes. |

- **Boundary Conditions**: If no workspace file exists, returns default empty group tree.
- **Return Fields**: `groups` (hierarchical array of group nodes containing `id`, `name`, `groupType`, `memberCount`, optional `children`, optional `rules`).
- **Error Codes**: `LIMIT_EXCEEDED`, `DATA_UNAVAILABLE`.

---

### 6.3 `sa_get_candles`
Returns chronological OHLCV bars for one symbol and timeframe (`D1`, `W1`, `MN1`), exactly as stored.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol (`.` and `-` interchangeable). |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Bar duration. |
| `count` | `integer` | No | `200` | Min: `1`, Max: `10000` | - | Bar count. |
| `startDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Inclusive start date. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Inclusive end date. |
| `completeBarsOnly` | `boolean` | No | `false` | - | - | `W1`/`MN1` only: drops incomplete final period bar. |

- **Boundary Conditions**: If `startDate > endDate`, raises `INVALID_ARGUMENT`. If data file has fewer bars than `count`, returns all available bars.
- **Return Fields**: `symbol` (`string`), `timeFrame` (`string`), `candles` (array of `{timestamp, open, high, low, close, volume}`), `lastBarPartial` (`bool`), `droppedLeadingBar` (`bool`).
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `DATA_UNAVAILABLE`.

---

### 6.4 `sa_get_ticker_metadata`
Returns locally stored metadata (company names, region, sector, industry, currency, fundamentals, personal annotations, and newest bar date).

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbols` | `string[]` | Yes | - | MinItems: `1`, MaxItems: `100` | - | Array of ticker symbols. |

- **Boundary Conditions**: Duplicate symbols are deduplicated after canonicalization. Missing symbols return records with `found: false`.
- **Return Fields**: `results` (dictionary keyed by symbol containing profile, fundamentals, strategy notes).
- **Error Codes**: `INVALID_ARGUMENT`, `LIMIT_EXCEEDED`, `DATA_UNAVAILABLE`.

---

### 6.5 `sa_get_capabilities`
Returns technical indicator catalog, parameter definitions, output series, supported timeframes, and screener capabilities.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `category` | `string` | No | `null` | - | - | Filter by indicator category (e.g., `Trend`, `Oscillator`). |
| `indicatorTypeFilter` | `string` | No | `null` | - | - | Filter by indicator name. |
| `includeDetails` | `boolean` | No | `false` | - | - | Inlines parameter bounds and output series names. |
| `includeScreenerCatalog` | `boolean` | No | `false` | - | - | Inlines screener column catalog, criteria, and operators. |

- **Boundary Conditions**: Filter mismatch returns empty indicator array without error.
- **Return Fields**: `indicators` (array), `supportedTimeFrames` (array), `periodUnit` (`"bars"`), optional `screenerCatalog`, optional `execution`.
- **Error Codes**: `INVALID_ARGUMENT`.

---

### 6.6 `sa_get_settings`
Returns allow-listed operational settings shaping tool boundaries.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `section` | `string` | No | `null` | - | `General`, `Screener`, `Backtest`, `Chart` | Filter by section name. |

- **Boundary Conditions**: Omission of `section` returns all allow-listed sections. File paths and passwords are never returned.
- **Return Fields**: `settings` (object keyed by section name containing setting properties).
- **Error Codes**: `INVALID_ARGUMENT`.

---

### 6.7 `sa_calculate_indicators`
Calculates up to 16 indicators on stored bars using the core indicator pipeline. Supports indicator chaining and cross-ticker comparisons.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol. |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Bar duration. |
| `indicators` | `object[]` | Yes | - | MinItems: `1`, MaxItems: `16` | - | Array of indicator specification objects. |
| `indicators[].id` | `string` | Yes | - | - | - | Client-assigned identifier. |
| `indicators[].type` | `string` | Yes | - | - | - | Indicator type name from `sa_get_capabilities`. |
| `indicators[].parameters` | `object` | No | `{}` | - | - | Key-value dictionary of indicator parameters. |
| `indicators[].priceSource` | `string` | No | `null` | - | `Close`, `Open`, `High`, `Low`, etc. | Price series input. |
| `indicators[].sourceIndicatorId` | `string` | No | `null` | - | - | ID of upstream indicator feeding input series. |
| `indicators[].sourceOutputSeriesName` | `string` | No | `null` | - | - | Series name of upstream indicator. |
| `indicators[].dynamicPeriodIndicatorId`| `string` | No | `null` | - | - | ID of indicator feeding adaptive period. |
| `indicators[].dynamicPeriodOutputSeriesName` | `string` | No | `null` | - | - | Series name of dynamic period indicator. |
| `lookbackBars` | `integer` | No | `10000` | Min: `1`, Max: `10000` | - | Calculation window for warm-up. |
| `outputTailCount` | `integer` | No | `50` | Min: `1`, Max: `500` | - | Trailing bars returned. |

- **Boundary Conditions**: If bar count is insufficient for warm-up, returns array with `leadingNullCount`. If all computed values are null, includes `allNullMessage`. Circular indicator dependencies raise `INVALID_ARGUMENT`.
- **Return Fields**: `symbol`, `timeFrame`, `timestamps` (array of `yyyy-MM-dd`), `results` (keyed by indicator ID with series arrays, `projectedValues`, `leadingNullCount`).
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `UNKNOWN_INDICATOR`, `INVALID_INDICATOR_PARAMETER`, `INVALID_OUTPUT_SERIES`, `INSUFFICIENT_MARKET_DATA`.

---

### 6.8 `sa_run_screener`
Evaluates a condition tree against daily bars (`D1`) of a symbol universe.

- **Heavy**: `true` (60s timeout)
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `conditionTree` | `object` | No | `null` | - | - | Condition tree object. Mutually exclusive with `templateId`. |
| `templateId` | `string` | No | `null` | - | - | Saved screener template GUID. Mutually exclusive with `conditionTree`. |
| `symbols` | `string[]` | No | `null` | MinItems: `1`, MaxItems: `500` | - | Specific universe. Mutually exclusive with `groupId`. |
| `groupId` | `string` | No | `null` | - | - | Workspace group GUID. Mutually exclusive with `symbols`. |
| `timeFrame` | `string` | No | `D1` | - | `D1` | Only `D1` supported. |
| `includeMatchedValues` | `boolean` | No | `false` | - | - | Returns evaluated left/right comparison values. |
| `maxReturnedMatches` | `integer` | No | `200` | Min: `1`, Max: `200` | - | Maximum matched symbols returned. |

- **Boundary Conditions**: Exactly one of `conditionTree` or `templateId` must be specified. Omitting both `symbols` and `groupId` screens the entire registered database.
- **Return Fields**: `matchedSymbols` (`string[]`), `matchedSymbolCount` (`int`), `truncated` (`bool`), optional `matchedValues`.
- **Error Codes**: `INVALID_ARGUMENT`, `UNSUPPORTED_TIMEFRAME`, `TEMPLATE_NOT_FOUND`, `GROUP_NOT_FOUND`, `LIMIT_EXCEEDED`, `EXECUTION_TIMEOUT`.

---

### 6.9 `sa_list_templates`
Lists saved screener and backtest condition templates.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `templateType` | `string` | No | `screener` | - | `screener`, `backtestCondition` | Category of templates to retrieve. |

- **Boundary Conditions**: Unreadable or corrupted template files are omitted from the listing and tallied in `unreadableFileCount`.
- **Return Fields**: `templateType`, `templates` (array of template objects), `unreadableFileCount` (`int`).
- **Error Codes**: `INVALID_ARGUMENT`, `DATA_UNAVAILABLE`.

---

### 6.10 `sa_run_backtest`
Executes an in-memory backtest simulation on daily, weekly or monthly bars using the Legacy execution model.

- **Heavy**: `true` (60s timeout)
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Target ticker symbol. |
| `timeFrame` | `string` | No | `D1` | - | `D1`, `W1`, `MN1` | Time frame of the bars to simulate (`D1`, `W1`, `MN1`). Default `D1`. |
| `conditions` | `object` | No | `null` | - | - | Entry/exit/reverse conditions. Mutually exclusive with `templateId`. |
| `templateId` | `string` | No | `null` | - | - | Backtest template GUID. Mutually exclusive with `conditions`. |
| `startDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Evaluation start date. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Evaluation end date. |
| `account` | `object` | No | `{}` | - | - | Account parameters (`initialCapital`, `sizingModel`, `slippageRatio`, etc.). |
| `riskManagement` | `object` | No | `null` | - | - | Stop-loss and take-profit ratios (`stopLossRatio`, `takeProfitRatio`). |
| `report` | `object` | No | `{}` | - | - | Reporting parameters (`annualRiskFreeRate`, `annualMar`, `bootstrapSeed`). |
| `maxReturnedTrades` | `integer` | No | `200` | Min: `0`, Max: `200` | - | Max trades listed in trade log. `0` suppresses trade list. |

- **Boundary Conditions**: If `templateId` is supplied, `riskManagement` must be omitted (template's internal risk settings govern). `startDate > endDate` raises `INVALID_ARGUMENT`. Conditions side timeframes must match the evaluated `timeFrame`.
- **Return Fields**: `symbol`, `timeFrame`, `metrics` (dictionary of valid metrics), `trades` (array of closed trades), `tradeCount` (`int`), `truncated` (`bool`), `warnings` (array of warning codes), `runFingerprint` (`string`).
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `TEMPLATE_NOT_FOUND`, `LIMIT_EXCEEDED`, `EXECUTION_TIMEOUT`.

---

### 6.11 `sa_calculate_reverse_watch`
Calculates the Reverse Watch price-volume curve, phase sectors (Phase 1-8), and bounding window coordinates.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol. |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Bar duration. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | End date of calculation. |
| `period` | `integer` | No | `25` | Min: `1` | - | Moving average smoothing period. |
| `isMaBased` | `boolean` | No | `true` | - | - | True: averages price and volume; False: raw values. |
| `isLogScaleVolume` | `boolean` | No | `false` | - | - | True: applies log10 to volume. |
| `dataCount` | `integer` | No | `25` | Min: `1` | - | Window size for positioning bounds. |
| `lookbackBars` | `integer` | No | `10000` | Min: `1`, Max: `10000` | - | Historical bars loaded. |
| `outputTailCount` | `integer` | No | `50` | Min: `1`, Max: `500` | - | Trailing points returned. |

- **Boundary Conditions**: Insufficient historical bars for `period` results in empty points array.
- **Return Fields**: `symbol`, `timeFrame`, `points` (array of `{timestamp, price, volume, phase, pricePosition, volumePosition}`).
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `DATA_UNAVAILABLE`.

---

### 6.12 `sa_calculate_chart_series`
Computes non-time-series and specialized charts (HeikinAshi, Renko, Kagi, PointAndFigure, ThreeLineBreak) including sizing and breakout pattern analysis.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbol` | `string` | Yes | - | - | - | Ticker symbol. |
| `timeFrame` | `string` | Yes | - | - | `D1`, `W1`, `MN1` | Base candle timeframe. |
| `chartType` | `string` | Yes | - | - | `HeikinAshi`, `Renko`, `Kagi`, `PointAndFigure`, `ThreeLineBreak` | Non-time-series chart type. |
| `endDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Calculation end date. |
| `lookbackBars` | `integer` | No | `10000` | Min: `1`, Max: `10000` | - | Base bars loaded. |
| `outputTailCount` | `integer` | No | `50` | Min: `1`, Max: `500` | - | Trailing units returned. |
| `sizingMode` | `string` | No | `Manual` | - | `Manual`, `Atr`, `Percentage` | Sizing calculation model. |
| `manualSize` | `number` | No | `null` | Min: `> 0` | - | Absolute brick/box size. |
| `reversalAmount` | `number` | No | `null` | Min: `> 0` | - | Absolute reversal amount for Point & Figure. |
| `atrPeriod` | `integer` | No | `14` | Min: `1` | - | ATR period when `sizingMode` is `Atr`. |
| `atrMultiplier` | `number` | No | `1.0` | Min: `> 0` | - | ATR scale multiplier. |
| `includePatterns` | `boolean` | No | `false` | - | - | Detects breakouts (Double, Triple, Trendline, Triangle). |

- **Boundary Conditions**: Non-positive sizes raise `INVALID_ARGUMENT`.
- **Return Fields**: `chartType`, `effectiveSize`, `units` (calculated bricks/columns), optional `patterns`.
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `UNSUPPORTED_TIMEFRAME`, `DATA_UNAVAILABLE`.

---

### 6.13 `sa_check_portfolio_rl_preflight`
Performs read-only validation across 2 to 100 tickers to check feasibility for portfolio reinforcement learning.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `symbols` | `string[]` | Yes | - | MinItems: `2`, MaxItems: `100` | - | Portfolio universe tickers. |
| `startDate` | `string` | No | `null` | `yyyy-MM-dd` | - | Candidate start date. |
| `endDateExclusive` | `string` | No | `null` | `yyyy-MM-dd` | - | First date outside evaluation. |
| `symbolHoldout` | `boolean` | No | `true` | - | - | Verifies train/holdout split feasibility. |
| `mode` | `string` | No | `Standard` | - | `Standard`, `ShortHistory` | Evaluation mode. |
| `maxAssetWeightPercent` | `integer` | No | `100` | Min: `1`, Max: `100` | - | Maximum portfolio asset weight cap. |

- **Boundary Conditions**: Providing fewer than 2 symbols raises `INVALID_ARGUMENT`.
- **Return Fields**: `isFeasible` (`bool`), `commonDateRange`, `unionCalendarBars` (`int`), `perSymbolReadiness` (dictionary).
- **Error Codes**: `INVALID_ARGUMENT`, `LIMIT_EXCEEDED`, `DATA_UNAVAILABLE`.

---

### 6.14 `sa_check_rl_playback_preflight`
Verifies replay feasibility of an RL model generation on stored bars.

- **Heavy**: `false`
- **Arguments**:

| Argument | Type | Req | Default | Bounds | Allowed Values | Description |
|---|---|---|---|---|---|---|
| `modelId` | `string` | Yes | - | - | - | Registered model generation GUID or `"active"`. |
| `symbol` | `string` | No | `null` | - | - | Target ticker (defaults to model's trained symbol). |

- **Boundary Conditions**: Unknown `modelId` returns `JOB_NOT_FOUND` or `ARTIFACT_NOT_FOUND`.
- **Return Fields**: `isReplayable` (`bool`), `earliestAnchorDate` (`string`), `latestAnchorDate` (`string`), `episodeHorizon` (`128`), `observationWindow` (`64`).
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `JOB_NOT_FOUND`, `ARTIFACT_NOT_FOUND`.

---

## 7. Execution Tools Reference (7 Tools, `--enable-execution` Required)

Opt-in execution tools operating exclusively within an ephemeral OS temporary sandbox directory.

### 7.1 `sa_start_training`
Starts an asynchronous supervised ML training job in the temporary sandbox.

- **Arguments**:
  - `symbols` (`string[]`, required, 1-100 items): Training tickers.
  - `framework` (`string`, required, enum: `TrainingFramework`): E.g., `LightGbm`, `Torch`.
  - `architecture` (`string`, required): Architecture identifier from `sa_get_capabilities`.
  - `windowSize` (`integer`, required, min: 1): Feature lookback bars.
  - `horizon` (`integer`, required, min: 1): Prediction target horizon bars.
  - `targetType` (`string`, optional, default: `Classification`): `Classification` or `Regression`.
  - `splits` (`integer`, optional, default: 5, min: 2): Walk-forward split count.
  - `fixedZScore` (`boolean`, optional, default: false): Population Z-score standardization.
  - `hyperparameters` (`object`, optional): Algorithm-specific hyperparameter map.
- **Return Fields**: `jobId` (`GUID`), `status` (`Queued` or `Running`), `artifactsLocation` (`"os-temp"`).
- **Error Codes**: `INVALID_ARGUMENT`, `EXECUTION_BUSY`, `PYTHON_ENV_MISSING`, `SANDBOX_PATH_TOO_LONG`.

---

### 7.2 `sa_get_training_status`
Polls the execution status and metric progress of a training job.

- **Arguments**:
  - `jobId` (`string`, required): Job GUID returned by `sa_start_training`.
- **Return Fields**: `jobId`, `status` (`Queued`, `Running`, `Completed`, `Failed`, `Cancelled`), `progressPercentage` (`int`), `foldMetrics` (array), `errorReason` (optional string).
- **Error Codes**: `INVALID_ARGUMENT`, `JOB_NOT_FOUND`.

---

### 7.3 `sa_cancel_training`
Requests cooperative cancellation of an active training job.

- **Arguments**:
  - `jobId` (`string`, required): Job GUID to cancel.
- **Return Fields**: `jobId`, `cancelled` (`bool`), `message` (`string`).
- **Error Codes**: `INVALID_ARGUMENT`, `JOB_NOT_FOUND`.

---

### 7.4 `sa_start_rl_training`
Starts a single-symbol reinforcement learning training process.

- **Arguments**:
  - `symbol` (`string`, required): Ticker symbol.
  - `algorithm` (`string`, required, enum: `RlAlgorithm`): E.g., `PPO`, `DQN`.
  - `episodes` (`integer`, required, min: 1, max: 10000): Episode count.
  - `hyperparameters` (`object`, optional): Hyperparameter dictionary.
- **Return Fields**: `jobId` (`GUID`), `status` (`Running`).
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `EXECUTION_BUSY`, `PYTHON_ENV_MISSING`.

---

### 7.5 `sa_start_portfolio_rl_training`
Starts a multi-asset portfolio reinforcement learning training process.

- **Arguments**:
  - `symbols` (`string[]`, required, minItems: 2, maxItems: 100): Universe tickers.
  - `algorithm` (`string`, required): E.g., `PPO`.
  - `episodes` (`integer`, required, min: 1, max: 10000): Episode count.
  - `hyperparameters` (`object`, optional): Hyperparameters.
- **Return Fields**: `jobId` (`GUID`), `status` (`Running`).
- **Error Codes**: `INVALID_ARGUMENT`, `LIMIT_EXCEEDED`, `EXECUTION_BUSY`, `PYTHON_ENV_MISSING`.

---

### 7.6 `sa_run_onnx_prediction`
Executes forward-pass inference on stored candles using an ONNX model artifact from the temporary sandbox.

- **Arguments**:
  - `modelId` (`string`, required): Model generation GUID.
  - `symbol` (`string`, required): Ticker symbol.
  - `count` (`integer`, optional, default: 50, min: 1): Number of bars to predict.
- **Return Fields**: `modelId`, `symbol`, `predictions` (array of `{timestamp, prediction, confidence, probabilities}`).
- **Error Codes**: `INVALID_ARGUMENT`, `SYMBOL_NOT_FOUND`, `ARTIFACT_NOT_FOUND`, `LIMIT_EXCEEDED`.

---

### 7.7 `sa_get_rl_playback`
Retrieves step-level episode playback (actions, state representations, portfolio weights, rewards, and Q-values).

- **Arguments**:
  - `modelId` (`string`, required): Model generation GUID.
  - `anchorDate` (`string`, required, format: `yyyy-MM-dd`): Starting bar date.
  - `symbol` (`string`, optional): Target symbol.
- **Return Fields**: `modelId`, `anchorDate`, `steps` (array of `{barIndex, timestamp, action, reward, portfolioWeights, qValues}`).
- **Error Codes**: `INVALID_ARGUMENT`, `JOB_NOT_FOUND`, `ARTIFACT_NOT_FOUND`, `SYMBOL_NOT_FOUND`.
