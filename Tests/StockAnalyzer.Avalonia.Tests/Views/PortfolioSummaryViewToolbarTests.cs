using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The two entry buttons above the portfolio tabs run the one add-transaction command with the type the dialog opens with:
/// Cash Flow opens it as a Deposit (the type can be switched to Withdrawal), Add Trade as a Long. Each takes its text from a localization key.
/// The command itself (dialog type per parameter, unknown parameters ignored) is covered by PortfolioSummaryViewModelTests.
/// </summary>
public class PortfolioSummaryViewToolbarTests
{
    private static string ViewSource() =>
        File.ReadAllText(Path.Combine(new[] { TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "Views", "PortfolioSummaryView.axaml" }.ToArray()));

    private static Match Button(string name, string key, string parameter) =>
        Regex.Match(ViewSource(),
            $@"<Button x:Name=""{name}""\s+Content=""\{{l:Localize {key}\}}""\s+Command=""\{{Binding AddTransactionCommand\}}""\s+CommandParameter=""{parameter}""",
            RegexOptions.Singleline);

    [Fact]
    public void CashFlowButton_RunsTheAddCommandAsADeposit() => Assert.True(Button("CashFlowButton", "Portfolio_Btn_CashFlow", "Deposit").Success);

    [Fact]
    public void AddTradeButton_RunsTheAddCommandAsALong() => Assert.True(Button("AddTradeButton", "Portfolio_Btn_AddTrade", "Long").Success);

    [Fact]
    public void TheTabsSitBelowTheButtons_InTheSameColumn()
    {
        var source = ViewSource();

        Assert.Contains("<Grid Grid.Column=\"2\" RowDefinitions=\"Auto,*\">", source);
        Assert.Contains("<TabControl Grid.Row=\"1\"", source);
    }
}
