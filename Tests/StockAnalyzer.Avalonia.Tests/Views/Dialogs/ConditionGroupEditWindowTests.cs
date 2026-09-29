using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>The Add/Edit Group dialog follows SA_UI_SUBWINDOW_STANDARD: header, name + AND/OR body, [OK] (Enter) and [Cancel] (Esc) footer.</summary>
public class ConditionGroupEditWindowTests
{
    private const int NameLimit = 12;

    private static string? Id(global::Avalonia.Controls.Control control) => global::Avalonia.Automation.AutomationProperties.GetAutomationId(control);

    private static ConditionGroupEditWindow Show(ConditionGroupEditRequest request)
    {
        var window = new ConditionGroupEditWindow { DataContext = new ConditionGroupEditDialogViewModel(request) };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, string id) where T : global::Avalonia.Controls.Control
        => window.GetVisualDescendants().OfType<T>().Single(c => Id(c) == id);

    [AvaloniaFact]
    public void AddDialog_ShowsTheAddTitle_TheNameBoxLimitedToTheConfiguredLength_AndTheOperatorChoice()
    {
        ConditionGroupEditWindow window = Show(new ConditionGroupEditRequest(IsNew: true, null, LogicalOperator.And, AllowName: true, NameLimit));
        try
        {
            Assert.True(Find<TextBlock>(window, "ConditionGroup_Title_Add").IsEffectivelyVisible);
            Assert.False(Find<TextBlock>(window, "ConditionGroup_Title_Edit").IsEffectivelyVisible);
            TextBox name = Find<TextBox>(window, "ConditionGroup_Name");
            Assert.True(name.IsEffectivelyVisible);
            Assert.Equal(NameLimit, name.MaxLength);
            Assert.True(Find<RadioButton>(window, "ConditionGroup_Operator_And").IsChecked);
            Assert.False(Find<RadioButton>(window, "ConditionGroup_Operator_Or").IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EditDialog_ShowsTheEditTitle_AndTheCurrentValues()
    {
        ConditionGroupEditWindow window = Show(new ConditionGroupEditRequest(IsNew: false, "MA", LogicalOperator.Or, AllowName: true, NameLimit));
        try
        {
            Assert.True(Find<TextBlock>(window, "ConditionGroup_Title_Edit").IsEffectivelyVisible);
            Assert.Equal("MA", Find<TextBox>(window, "ConditionGroup_Name").Text);
            Assert.True(Find<RadioButton>(window, "ConditionGroup_Operator_Or").IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EditingARoot_HidesTheNameBox()
    {
        ConditionGroupEditWindow window = Show(new ConditionGroupEditRequest(IsNew: false, null, LogicalOperator.And, AllowName: false, NameLimit));
        try
        {
            Assert.False(Find<TextBox>(window, "ConditionGroup_Name").IsEffectivelyVisible);
            Assert.True(Find<RadioButton>(window, "ConditionGroup_Operator_And").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Footer_HasOkAsTheDefaultAndCancelAsTheCancelButton_InThatOrder()
    {
        ConditionGroupEditWindow window = Show(new ConditionGroupEditRequest(IsNew: true, null, LogicalOperator.And, AllowName: true, NameLimit));
        try
        {
            Button ok = Find<Button>(window, "ConditionGroup_Ok");
            Button cancel = Find<Button>(window, "ConditionGroup_Cancel");

            Assert.True(ok.IsDefault);
            Assert.True(cancel.IsCancel);
            Assert.Same(ok.Parent, cancel.Parent);
            var footer = (Panel)ok.Parent!;
            Assert.True(footer.Children.IndexOf(ok) < footer.Children.IndexOf(cancel));
        }
        finally
        {
            window.Close();
        }
    }
}
