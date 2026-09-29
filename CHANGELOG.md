# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
