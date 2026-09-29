# Contributing to StockAnalyzer

Thank you for your interest in contributing to StockAnalyzer! We welcome contributions to make the application better, faster, and more robust.

> [!IMPORTANT]
> StockAnalyzer is primarily developed and maintained using AI coding assistants.
> If you want to contribute, we encourage you to bring your own AI tools to investigate bugs, implement logic, and write tests.
> Human code review is minimal — automated tests and CI pipelines act as the primary gatekeepers.

## Code of Conduct

Please be respectful and supportive to other contributors. We strive to maintain a friendly, welcoming community.

---

## Getting Started

### 1. Fork and Clone the Repository

```bash
git clone https://github.com/akuro17/StockAnalyzer.git
cd StockAnalyzer
```

### 2. Install .NET SDK

Install **.NET 8.0 SDK** or later. Verify the installation:

```bash
dotnet --version
```

### 3. Configure the Application

Copy the example configuration file inside the Avalonia project:

```bash
cp StockAnalyzer.Avalonia/appsettings.example.json StockAnalyzer.Avalonia/appsettings.json
```

> [!NOTE]
> `appsettings.json` is ignored by Git to keep your local paths secure.

### 4. Set Up the Python Backend

The backend data processing relies on Python 3.8 to 3.11.

#### Option A: Windows (Automatic)

No manual Python setup is needed. On the first run, the app automatically initializes a portable Python environment and installs all dependencies. The first launch may take a few minutes while `pip install` runs in the background.

#### Option B: macOS / Linux / Custom Paths (Manual)

Create a virtual environment, install dependencies, and update your configuration:

```bash
# Create and activate a virtual environment
python3 -m venv .venv
source .venv/bin/activate   # On Windows: .venv\Scripts\activate

# Install requirements
pip install -r StockAnalyzer.Python/requirements.txt
```

Then update `StockAnalyzer.Avalonia/appsettings.json` with the absolute path to the Python executable:

```json
"Python": {
  "PythonPath": "/absolute/path/to/your/StockAnalyzer/.venv/bin/python3"
}
```

### 5. Build and Run

```bash
dotnet build StockAnalyzer.sln
dotnet run --project StockAnalyzer.Avalonia/StockAnalyzer.Avalonia.csproj
```

---

## Development Guidelines

Follow the architectural standards documented in [`docs/SA_ARCHITECTURE_RULES.md`](docs/SA_ARCHITECTURE_RULES.md). Key principles:

- **MVVM Architecture**: Strictly follow the MVVM pattern. ViewModels must be platform-agnostic — prohibit Avalonia-specific types (`Brush`, `GridLength`, `Dispatcher`, etc.) inside ViewModels.
- **Dependency Injection**: All services must be injected via constructor. Prohibit `new XxxService()` inside ViewModels.
- **Decoupled Communication**: Use `IMessenger` (CommunityToolkit.Mvvm) for non-hierarchical ViewModel communication. Prohibit direct cross-ViewModel references.
- **Zero Allocations**: High-performance rendering code (e.g., Chart views) must follow zero-allocation practices. Avoid creating heap-allocated buffers inside hot paths.
- **No Hardcoding**: Prohibit magic numbers and string literals in logic. Externalize configuration via `appsettings.json` (C#) or `.env` (Python).
- **Localization**: All user-visible strings must be externalized to `en.json`. Use `{l:Localize Key}` in XAML and `LocalizationManager.Instance["Key"]` in ViewModels. English-only code — prohibit Japanese characters in source code, comments, or strings.
- **StyleCop Compliance**: Code formatting must comply with the StyleCop configuration (`stylecop.json`). Run the format command locally before submitting.

For complete technical standards, see the documentation hub: [`DEVELOPMENT_MAP.md`](DEVELOPMENT_MAP.md).

---

## Pull Request Process

1. **Create a Feature Branch**: Branch off `main` with a descriptive name.
2. **Write Unit Tests**: Ensure new logic and indicator computations are verified with unit tests under the `Tests/` project. **A pull request without tests is not accepted.**
3. **Verify Locally**:

   ```bash
   # Build the solution
   dotnet build StockAnalyzer.sln

   # Run C# unit and integration tests (excludes UI tests)
   dotnet test Tests/StockAnalyzer.Tests.csproj --filter "FullyQualifiedName!~UITests"

   # Run Python tests (if Python code was modified)
   cd StockAnalyzer.Python
   pytest
   ```

4. **Submit the PR**: Fill out the pull request template carefully and check off the validation requirements list.
5. **CI/CD**: Every pull request must pass the automated pipelines. Code that fails these checks will not merge.

> [!NOTE]
> Use clear commit message prefixes, for example: `fix:`, `feat:`, `refactor:`, `test:`, `docs:`.

---

## Indicator Contributions

- Implement it under `StockAnalyzer.Core/` following the `IIndicator` interface.
- Register it in `IndicatorRegistry` and add the enum value to `IndicatorType`.
- Add unit tests in `Tests/` using `IndicatorRegistry.TryCreate()` — do **not** create mock stubs for existing types.
- Use real `CandleData` records in tests. See existing batch test files (`Tests/Batch*Tests.cs`) for patterns.

---

## License

All contributions are subject to the **MIT License**. By submitting a pull request, you agree to release your contribution under the project's MIT License.
