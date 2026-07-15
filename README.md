# StockAnalyzer

StockAnalyzer is an open-source, cross-platform desktop application for stock analysis, technical charting, screening, and portfolio management. It is built with .NET 8 and Avalonia UI, leveraging Python to provide advanced analytical services.

---

## Supported Platforms

| OS | Architecture | Python Setup |
|---|---|---|
| Windows 10 / 11 | x64 | Automatic (embedded Python installer included) |
| macOS (Intel & Apple Silicon) | x64 / ARM64 | Manual (requires system Python 3.8+ and configuration) |
| Linux (Ubuntu / Debian-based) | x64 | Manual (requires system Python 3.8+ and configuration) |

---

## Requirements

- .NET SDK: 8.0 or later
- Python: 3.8 to 3.11 (required for data retrieval and indicators)
  - Core libraries: `yfinance`, `pandas`, `polars`, `pyarrow`, `onnxruntime`.

---

## Project Structure

```text
StockAnalyzer/
├── StockAnalyzer.Avalonia/  # Desktop UI (Views, ViewModels, Assets)
├── StockAnalyzer.Core/      # Business logic, math, and Named Pipe IPC
├── StockAnalyzer.Python/    # Python scripts for data ingestion and indicators
└── Tests/                  # Unit, Integration, and UI Automation tests
```

---

## Getting Started

### 1. Clone the Repository
```bash
git clone https://github.com/akuro17/StockAnalyzer.git
cd StockAnalyzer
```

### 2. Configuration
Windows users can run the app with default settings out of the box. For custom paths or non-Windows environments, you need an active configuration file:

```bash
# Copy the example configuration
cp StockAnalyzer.Avalonia/appsettings.example.json StockAnalyzer.Avalonia/appsettings.json
```

> [!IMPORTANT]
> `appsettings.json` is ignored by Git to keep your local paths secure.

### 3. Python Environment

#### Option A: Windows (Automatic)
No manual setup is needed. The app automatically initializes Python and installs dependencies on the first run. (The first launch may take a few minutes to run `pip install` in the background).

#### Option B: macOS / Linux / Custom Paths (Manual)
You need to create a virtual environment, install dependencies, and update your configuration.

```bash
# Create and activate a virtual environment
python3 -m venv .venv
source .venv/bin/activate

# Install requirements
pip install -r StockAnalyzer.Python/requirements.txt
```

Next, update `StockAnalyzer.Avalonia/appsettings.json` with the absolute path to the python executable in your virtual environment:

```json
"Python": {
  "PythonPath": "/absolute/path/to/your/StockAnalyzer/.venv/bin/python3"
}
```

### 4. Run the Application

```bash
dotnet build StockAnalyzer.sln
dotnet run --project StockAnalyzer.Avalonia/StockAnalyzer.Avalonia.csproj
```

---

## Workspace Layout

On the first launch, the app creates a `Data/` directory to store your workspace files:

```text
Data/
├── Daily/      # Daily historical data (Parquet)
├── Weekly/     # Weekly aggregated data
├── Monthly/    # Monthly aggregated data
├── Metadata/   # Symbol info and metadata
├── Config/     # User settings and UI state
└── Portfolios/ # Portfolio data
```

---

## Testing

StockAnalyzer uses xUnit for C# and pytest for Python.

### C# Tests
```bash
# Run unit and integration tests (excludes UI tests)
dotnet test Tests/StockAnalyzer.Tests.csproj --filter "FullyQualifiedName!~UITests"
```
> [!NOTE]
> UI Tests are excluded by default as they require a GUI and are generally not suitable for headless CI environments.

### Python Tests
```bash
cd StockAnalyzer.Python
pytest
```

---

## FAQ & Troubleshooting

*   Why does the first launch take longer on Windows?
    The app is downloading a portable Python environment and installing packages. Subsequent startups are immediate. Check the `logs/` directory if you want to monitor the `pip` output.
*   Can I use my own Python installation?
    Yes. Update the `PythonPath` in `appsettings.json` to point to your preferred Python 3.8-3.11 binary.
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