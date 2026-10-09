using System.Runtime.CompilerServices;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>
/// Gives the process-wide <see cref="LocalizationManager"/> a defined initial state (English) before any test runs. Tests that only read
/// localized text (Drawing, Notes, Ticker list) assume English strings are loaded, but nothing in a test assembly loads them: they used
/// to pass only when a locale-changing class happened to run first. Classes that change the language belong to the serialized
/// "LocalizationSharedState" collection and restore it, so the state is English whenever a reader runs.
/// </summary>
internal static class LocalizationTestBootstrap
{
    [ModuleInitializer]
    internal static void InitializeEnglish() => LocalizationManager.Instance.Initialize("en");
}
