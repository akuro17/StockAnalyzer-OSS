# StockAnalyzer

StockAnalyzer is an open-source, cross-platform desktop application for stock analysis, technical charting, screening, and portfolio management. It is built with .NET 8 and Avalonia UI, leveraging Python to provide advanced analytical services.

---

## Supported Platforms

| OS | Architecture | Python Setup |
|---|---|---|
| Windows 10 / 11 | x64 | Automatic (embedded Python installer included) |
| macOS (Intel & Apple Silicon) | x64 / ARM64 | Manual (requires system Python 3.13 (3.13.17+) and configuration) |
| Linux (Ubuntu / Debian-based) | x64 | Manual (requires system Python 3.13 (3.13.17+) and configuration) |

---

## Requirements

- .NET SDK: 8.0 or later
- Python: 3.13 (3.13.17 or later) with pip 26.2.2 or later; Windows includes automatic embedded Python
  - Core libraries: `yfinance`, `pandas`, `polars`, `pyarrow`.

---

## Project Structure

```text
StockAnalyzer/
├── StockAnalyzer.Avalonia/  # Desktop UI (Views, ViewModels, Assets)
├── StockAnalyzer.Core/      # Business logic, math, and Named Pipe IPC
├── StockAnalyzer.McpServer/ # Read-only MCP server for AI clients (stdio)
├── StockAnalyzer.Python/    # Python scripts for data ingestion and indicators
└── Tests/                  # Unit, Integration, and UI Automation tests
```

---

## Getting Started

### 1. Clone the Repository
```bash
git clone https://github.com/akuro17/StockAnalyzer-OSS.git
cd StockAnalyzer-OSS
```

### 2. Configuration
Windows users can run the app with default settings out of the box. For custom paths or non-Windows environments, you need an active configuration file:

**macOS / Linux (Bash):**
```bash
cp StockAnalyzer.Avalonia/appsettings.example.json StockAnalyzer.Avalonia/appsettings.json
```

**Windows (PowerShell):**
```powershell
Copy-Item StockAnalyzer.Avalonia\appsettings.example.json StockAnalyzer.Avalonia\appsettings.json
```

> [!IMPORTANT]
> `appsettings.json` is ignored by Git to keep your local paths secure.

### 3. Python Environment

#### Option A: Windows (Automatic)
No manual setup is needed. The app automatically initializes Python and installs dependencies on the first run. (The first launch may take a few minutes to run `pip install` in the background).

#### Option B: macOS / Linux / Custom Paths (Manual)
You need to create a virtual environment, install dependencies, and update your configuration.

**macOS / Linux (Bash):**
```bash
python3.13 -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade "pip>=26.2.2" certifi
pip install -r StockAnalyzer.Python/requirements.txt
```

**Windows (PowerShell):**
```powershell
py -3.13 -m venv .venv
.venv\Scripts\Activate.ps1
python -m pip install --upgrade "pip>=26.2.2" certifi
pip install -r StockAnalyzer.Python\requirements.txt
```

Next, update `StockAnalyzer.Avalonia/appsettings.json` with the absolute path to the python executable in your virtual environment:

```json
"Python": {
  "PythonPath": "/absolute/path/to/your/StockAnalyzer-OSS/.venv/bin/python3"
}
```

### 4. Run the Application

**macOS / Linux (Bash):**
```bash
dotnet build StockAnalyzer.sln
dotnet run --project StockAnalyzer.Avalonia/StockAnalyzer.Avalonia.csproj
```

**Windows (PowerShell):**
```powershell
dotnet build StockAnalyzer.sln
dotnet run --project StockAnalyzer.Avalonia\StockAnalyzer.Avalonia.csproj
```

### 5. Initial Smoke Verification
Verify initial application startup:
1. Main window launches and displays the default chart layout.
2. The left-side Ticker List displays default symbols or allows creating a new Watchlist.
3. Selecting a symbol displays historical candle charts (first download may take a moment).

---

## Workspace Layout

On the first launch, the app creates a `Data/` directory to store your workspace files:

```text
Data/
├── Backtest/             # Backtest settings and exported reports
│   └── Reports/          # Exported backtest HTML/JSON reports
├── Config/               # User settings, themes, and UI workspace layouts
├── Daily/                # Daily historical OHLCV data (Parquet)
├── Drawings/             # Chart drawing objects and annotations
│   └── Icon/             # Custom icon stamps
├── DynamicPeriodDrivers/ # Dynamic period driver settings for adaptive indicators
├── Experiments/          # ML model training run experiment configs and metrics
├── IndicatorDefaults/    # Custom default parameters for indicators
├── Metadata/             # Symbol metadata and user strategy signal definitions
├── Models/               # ML prediction models (ONNX)
│   └── Generations/      # Versioned model generation store
├── Monthly/              # Monthly aggregated OHLCV data (Parquet)
├── Notes/                # User trading notes SQLite database (notes.db)
│   └── Attachments/      # Chart image attachments for notes
├── Portfolios/           # Portfolio positions, cash balances, and trade records
├── PredictionMonitoring/ # ML model prediction telemetry and health monitoring
├── SourceIndicators/     # Custom source indicator pipeline definitions
├── Templates/            # Centralized templates (Indicators, Screeners, Columns, etc.)
├── TrainingArtifacts/    # Training outputs: TW/ (Training Wizard models and checkpoints), RL/, RLPortfolio/
└── Weekly/               # Weekly aggregated OHLCV data (Parquet)
```

> [!TIP]
> For detailed instructions on chart drawing shortcut keys, editing controls, and configuring custom icon stamps in `Data/Drawings/Icon/`, see the [Drawing Tools Guide](https://github.com/akuro17/StockAnalyzer-OSS/blob/main/docs/DRAWING_TOOLS_GUIDE.md).

> [!TIP]
> For Model Context Protocol (MCP) server options, command-line usage, tool schemas, and copy-pasteable execution examples, see the [MCP Server Command Reference](https://github.com/akuro17/StockAnalyzer-OSS/blob/main/docs/MCP_SERVER_COMMAND_REFERENCE.md).

> [!TIP]
> For the settings of Training Wizard, Reinforcement Learning and Portfolio Reinforcement Learning and for how to use the files they create, see the [Training and Reinforcement Learning Guide](https://github.com/akuro17/StockAnalyzer-OSS/blob/main/docs/TRAINING_AND_REINFORCEMENT_LEARNING_GUIDE.md).

---

## Testing

StockAnalyzer uses xUnit for C# and pytest for Python.

### Cross-Platform C# Tests (Ubuntu, macOS, Windows)
```bash
# Run cross-platform core business logic and analysis tests
dotnet test Tests/StockAnalyzer.Core.Tests/StockAnalyzer.Core.Tests.csproj
```

### Windows-Only Tests (WPF / UI Automation)
```bash
# Run Windows-specific and FlaUI UI automation tests (requires active desktop session)
dotnet test Tests/StockAnalyzer.UITests/StockAnalyzer.UITests.csproj
```
> [!NOTE]
> UI Tests are excluded from automated CI workflows as they require an active display.

### Python Tests
Install development dependencies and run tests from the repository root:
```bash
pip install -r StockAnalyzer.Python/requirements-dev.txt
pytest Tests/test_*.py
```

---

## FAQ & Troubleshooting

*   Windows SmartScreen "Windows protected your PC" warning when running the executable.
This warning appears because the pre-built binaries generated on GitHub Actions are not digitally signed with a code signing certificate, which can be costly to obtain. To run the app, click "More info" on the warning dialog, then click "Run anyway". The warning will not appear again on subsequent launches of the same file.
*   Why does the first launch take longer on Windows?
    The app is downloading a portable Python environment and installing packages. Subsequent startups are immediate. Check the `logs/` directory if you want to monitor the `pip` output.
*   Can I use my own Python installation?
    Yes. Update the `PythonPath` in `appsettings.json` to point to your preferred Python 3.13 (3.13.17 or later) binary.
*   What is `trend_predictor.onnx`?
    This is the machine learning model used for predictions. A `dummy.onnx` is provided to allow the app to compile and start, but actual predictions will fail until you replace it with a properly trained model.
*   Python processes hang after a crash.
    If the app closes unexpectedly, kill lingering Python processes manually (`Stop-Process -Name python -Force` on PowerShell, or `pkill -f update_pipeline.py` on Bash).

---

## Acknowledgments & Licenses

This project relies on several key libraries:
- Avalonia UI (MIT)
- DuckDB.NET (MIT)
- ONNX Runtime (MIT)
- yfinance (Apache 2.0)

See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for a complete list and license texts.

---

## Disclaimer (Yahoo Finance API)

> [!WARNING]
> This software is provided "as-is". It connects to third-party endpoints using `yfinance`, an unofficial Yahoo Finance client.
> *   Use at your own risk. The maintainers are not responsible for service interruptions, IP blocks, or Terms of Service violations.
> *   This tool is intended for personal, educational, or research purposes. Complying with Yahoo Finance's API limits and Terms of Service is the sole responsibility of the user.

---

## AI Contribution Notice

> [!IMPORTANT]
> StockAnalyzer is primarily developed and maintained using AI coding assistants.
> As such, it does not have a traditional team of human maintainers. Users are encouraged to utilize AI coding assistants themselves to troubleshoot issues, fix bugs, or implement new features locally.
