# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Added 19 Skia series primitive renderers (SeriesPrimitiveRendererRegistry), 12 single-series render modes (SeriesRenderStyleCatalog), and per-series style and visibility controls with live chart sync (SeriesStyleEditorAdapter).
- Added Oklab heatmap color space and ramp generation with chroma-preserving sRGB gamut mapping and WCAG AA 4.5:1 tile text contrast (HeatmapColorSpace).
- Added MCP opt-in progressive discovery (--progressive-discovery, sa_search_tools, sa_invoke_tool), result handle listing (sa_list_result_handles), compact defaults (--compact-defaults), MaxResponseTokens response budgets, and pagination on result handles.
- Upgraded sa_evaluate_expressions to CalculatorVersion 8 with multi-step arima_forecast, data-structure operations, and expanded math/finance functions.
- Added customizable POC color for Fixed Range Volume Profile / Time-at-Price indicators and ranking outputs (includeRanking, rankingLimit) on sa_calculate_volume_profile.
- Added OHLCV bar validation diagnostics (invalidBarCount, datasetIdentity), backtest sizing/sample-size advisories, and flags for unfinished seasonality periods and currency mismatches.
- Added startup read-only mode when the Data folder is not writable, partial workspace restore protection, Tab Window drag reordering, and configurable Layout:MaxPanelTabs / Layout:MaxTabReorderDistance settings.

### Changed
- Unified ticker symbol canonicalization and normalization (TickerSymbol.Canonicalize, TickerHelper.NormalizeTicker) with strict 4-digit ASCII Japanese stock -T suffixing and support for continuous futures (=F) and currency pairs (=X).
- Split the MCP technical toolset into technical (6 core tools) and technical-extended (10 specialized tools), with matching prompt filtering in McpPromptRegistry.
- Decoupled heatmap tile rendering via HeatmapDrawOperation, unified DrawingThemeContext.ScreenGeometryEpsilon, enforced StrictEnumValidator.TryParseStrict, and aligned Change / Change % calculations to the previous close.
- Removed the silent %TEMP%\StockAnalyzer\ fallback when Data cannot be created, restricted tab drops to the tab-strip row, and prevented drags from starting on close buttons.

### Fixed
- Fixed OHLCV screening column matching via OutputName, Change % calculation when the previous close is negative, RSI formula consistency between RsiOversoldCondition and SQL translation, and Portfolio P&L % headers.
- Prevented exceptions when redocking into a full panel, removed [ThreadStatic] Skia resource leaks in ReverseWatchCurveWindow, and hardened MCP JSON and cancellation boundaries.

### Removed & Deprecated
- Removed public LayoutConstants.MaxPanelTabs, MAX_TAB_REORDER_DISTANCE, and MAX_TABS_PER_PANEL in favor of LayoutSettings, and marked IndicatorType.ThreeLineBreakSignal obsolete (deregistered from IndicatorFactory).

### Security
- Replaced Python.Included with SHA-256 verified embedded CPython 3.13.17 (python-3.13.17-embed-amd64.zip) and get-pip.py bootstrap (pip >= 26.2.2) via PythonEmbeddedDistribution.
