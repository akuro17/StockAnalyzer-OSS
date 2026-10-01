using System;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>An <see cref="IPanelTabFactory"/> whose result is chosen by the test (null result = creation failed).</summary>
public class MockPanelTabFactory : IPanelTabFactory
{
    public Func<string, WorkspaceViewItem?>? CreateTabFunc { get; set; }
    public WorkspaceViewItem? CreateTab(string id) => CreateTabFunc?.Invoke(id);
}
