using Avalonia;
using Avalonia.Controls;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>Shared layout assertions of the run-window footers (the blocking reason and the state labels sit side by side in a bounded cell).</summary>
internal static class FooterLayoutAssert
{
    /// <summary>The reason is laid out as wide as its own text needs: it was not squeezed (and so not trimmed) by the labels next to it.</summary>
    public static void ReasonIsShownInFull(TextBlock reason, string where) => Assert.True(ReasonProblem(reason, where) is null, ReasonProblem(reason, where));

    /// <summary>The description of how the reason is trimmed, or null when it is shown in full.</summary>
    public static string? ReasonProblem(TextBlock reason, string where)
    {
        var probe = new TextBlock { Text = reason.Text, FontSize = reason.FontSize, FontFamily = reason.FontFamily, FontWeight = reason.FontWeight, FontStyle = reason.FontStyle };
        probe.Measure(Size.Infinity);
        if (probe.DesiredSize.Width <= 0) return where + ": the reason has no text";
        return reason.Bounds.Width + 0.5 >= probe.DesiredSize.Width ? null : $"{where}: the reason is trimmed ({reason.Bounds.Width:F0} of {probe.DesiredSize.Width:F0})";
    }
}
