using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>
/// xUnit test classes run in parallel by default across different classes in this project.
/// DrawingThemeContext is process-wide static state; any test class that mutates it via a
/// SetXxxForTesting seam (see StockAnalyzer.Avalonia.Drawing.DrawingThemeContext) must opt into
/// this collection so it never runs concurrently with another test in the same collection --
/// otherwise one test's temporary override could be observed by an unrelated test running at the
/// same time, producing an execution-order-dependent flaky failure.
/// </summary>
[CollectionDefinition("DrawingThemeContext State", DisableParallelization = true)]
public class DrawingThemeContextTestCollection
{
}
