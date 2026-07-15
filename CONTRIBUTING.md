# Contributing to StockAnalyzer

Thank you for your interest in contributing to StockAnalyzer! We welcome contributions to make the application better, faster, and more robust.

## Code of Conduct

Please be respectful and supportive to other contributors. We strive to maintain a friendly, welcoming community.

## Getting Started

1. **Fork the Repository**: Create a personal fork of the project on GitHub.
2. **Setup Development Environment**:
   - Install .NET 8.0 SDK or later.
   - Configure your IDE (VS Code with C# Dev Kit, Visual Studio, or JetBrains Rider).
3. **Copy Configuration**:
   ```bash
   cp StockAnalyzer.Avalonia/appsettings.example.json StockAnalyzer.Avalonia/appsettings.json
   ```

## Development Guidelines

- **Architecture Rules**: Follow the MVVM architecture patterns defined in [SA_ARCHITECTURE_RULES.md](docs/SA_ARCHITECTURE_RULES.md).
- **Zero Allocations**: High-performance rendering code (e.g. Chart views) should follow zero-allocation practices. Avoid creating heap-allocated buffers inside hot paths.
- **StyleCop Rules**: Code format must comply with the stylecop configuration (`stylecop.json`).

## Pull Request Process

1. **Create a Branch**: Create a feature branch off of the `main` branch.
2. **Write Unit Tests**: Ensure new logic or indicator computations are verified with unit tests under the `Tests/` project.
3. **Verify Locally**:
   ```bash
   dotnet build StockAnalyzer.sln
   dotnet test Tests/StockAnalyzer.Tests.csproj --filter "FullyQualifiedName!~UITests"
   ```
4. **Submit PR**: Fill out the pull request template carefully. Check off the validation requirements list.

All contributions are subject to the MIT License.
