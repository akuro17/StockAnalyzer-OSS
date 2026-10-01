# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- **Tab Window tab reordering**: Tabs inside a Tab Window can be reordered by mouse drag like panel tabs (8 px drag threshold, the moved tab becomes selected, the order is saved with the workspace).
- **Layout settings**: `Layout:MaxPanelTabs` (default 16) and `Layout:MaxTabReorderDistance` (default 100) in `appsettings.json` / `appsettings.example.json`. An older `appsettings.json` without the section keeps the defaults; an invalid value (tab limit below 1, negative distance) fails at startup with an explanatory exception instead of being replaced.
- **Notice when a tab cannot return to a full panel**: Closing a Tab Window tab (or the window) while the target panel already holds the maximum number of tabs now shows one notice. The tabs stay in a Tab Window; tabs closed together from one window return to one window.

### Changed
- **Tab drop target**: Releasing a dragged tab over the content area of a panel or Tab Window no longer moves it to the end; only a release on the tab-strip row (outside any tab) does.
- **Tab Window close button**: Pressing the per-tab close button no longer starts a window move or a tab drag.

### Fixed
- **Redock into a full panel**: Returning a Tab Window tab to a panel that already held the maximum number of tabs exceeded the limit and threw; the tab now stays in a Tab Window (see the notice above).

### Removed (API)
- **`LayoutConstants.MaxPanelTabs`, `LayoutConstants.MAX_TAB_REORDER_DISTANCE` and `LayoutConstants.MAX_TABS_PER_PANEL`** (public constants in `StockAnalyzer.Core`): the limits are configuration now. Read them from `LayoutStateStore.MaxPanelTabs` / `LayoutStateStore.MaxTabReorderDistance`, or from `new LayoutSettings()` for the defaults. `MAX_TABS_PER_PANEL` (20) was unused and disagreed with the effective limit of 16. Code compiled against the removed constants must be updated.

---

## [1.0.0] - 2026-07-08

### Added
- **MIT LICENSE**: Added legal protection and fork terms for open-source distribution.
- **appsettings.example.json**: Configuration template for developers to easily copy and get started.
- **README.md**: Complete onboarding documentation including OS-specific setup and yfinance disclaimers.
- **CONTRIBUTING.md & SECURITY.md**: Community guidelines and security vulnerability reporting process.
- **Cross-Platform Auto-Directories**: Added auto-creation of data folders (`Daily`, `Weekly`, `Monthly`, `Metadata`, `Config`, `Portfolios`) on app startup to prevent directory-not-found exceptions on clean clone environments.

### Fixed
- **appsettings.json Fallbacks**: Fixed dependency injection startup errors when `appsettings.json` is missing. Fallbacks now resolve to code-defined default relative paths.
- **Allocation Tab Persistence**: Commented out the migration logic that forcefully added the Allocation tab to the Bottom panel on every startup, allowing user layout preferences (closing the tab) to persist properly.
