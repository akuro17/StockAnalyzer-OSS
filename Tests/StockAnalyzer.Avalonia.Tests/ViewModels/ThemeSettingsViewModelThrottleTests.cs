using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// A color change that arrives inside the throttle interval is applied later on the UI thread; when several arrive, the last one is what
/// ends up applied (each call cancels the delayed update of the call before it).
/// </summary>
public class ThemeSettingsViewModelThrottleTests
{
    private static async Task PumpUiThread(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task TheLastColorOfARapidSequenceIsApplied()
    {
        var themeManager = new ThemeManager();
        var vm = new ThemeSettingsViewModel(themeManager);
        var first = IndicatorColor.FromUInt(0xFF112233);
        var second = IndicatorColor.FromUInt(0xFF445566);
        var last = IndicatorColor.FromUInt(0xFF778899);

        vm.GridColor = first;     // the first change applies at once
        vm.GridColor = second;    // inside the throttle interval: delayed
        vm.GridColor = last;      // replaces the delayed one
        await PumpUiThread(TimeSpan.FromMilliseconds(500));

        Assert.Equal(last, themeManager.GetCurrentColors()[ThemeColorKey.Grid]);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task ADelayedColorIsNotAppliedAfterDispose()
    {
        var themeManager = new ThemeManager();
        var vm = new ThemeSettingsViewModel(themeManager);
        var before = themeManager.GetCurrentColors()[ThemeColorKey.Grid];

        vm.GridColor = IndicatorColor.FromUInt(0xFF010203);   // applies at once
        var afterFirst = themeManager.GetCurrentColors()[ThemeColorKey.Grid];
        vm.GridColor = IndicatorColor.FromUInt(0xFF040506);   // delayed
        vm.Dispose();
        await PumpUiThread(TimeSpan.FromMilliseconds(300));

        Assert.NotEqual(before, afterFirst);
        Assert.Equal(afterFirst, themeManager.GetCurrentColors()[ThemeColorKey.Grid]);
    }
}
