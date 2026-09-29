using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.Views.Notes;
using StockAnalyzer.Core.Models.Notes;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>sa_minimal_fix (Notes timeline): mouse-wheel scrolling must work over the blank space
/// beside the vertical reply-connector line between two reply-chained cards. Root cause: the
/// SidebarScrollViewerTheme's ScrollContentPresenter was not hit-testable over blank content, so the
/// wheel landed on the template Grid and never reached the presenter that handles it.</summary>
public class NoteTimelineView_ReplyChainWheelScrollTests
{
    private const double WindowWidth = 480;
    private const double WindowHeight = 400;
    private const int ReplyChainLength = 5;
    private const double WheelDeltaY = -3;

    /// <summary>Horizontal probe positions on the connector row: far left edge, far right edge, and
    /// offsets relative to the connector line's own X (left of it, on it, right of it).</summary>
    private static readonly double[] AbsoluteProbeXs = { 5.0, 400.0 };
    private static readonly double[] LineRelativeProbeXOffsets = { -20.0, 1.0, 30.0 };

    [AvaloniaFact]
    public async Task MouseWheel_ScrollsTimeline_OverBlankSpaceBesideReplyConnectorLine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_reply_wheel_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var view = new NoteTimelineView { DataContext = timeline };
            var window = new Window { Content = view, Width = WindowWidth, Height = WindowHeight };
            window.Show();
            try
            {
                var parentId = Guid.NewGuid();
                var now = DateTime.Now;
                await noteRepository.CreateAsync(new Note(parentId, "parent", now.AddMinutes(-30), now.AddMinutes(-30)));
                var previousId = parentId;
                for (var i = 0; i < ReplyChainLength; i++)
                {
                    var id = Guid.NewGuid();
                    await noteRepository.CreateAsync(new Note(id, $"reply {i}", now.AddMinutes(-20 + i), now.AddMinutes(-20 + i)) { ParentNoteId = previousId });
                    previousId = id;
                }
                await timeline.RefreshAsync();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var scrollViewer = view.FindControl<ScrollViewer>("TimelineScrollViewer")!;
                Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height, "test setup: content must overflow");

                var line = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ReplyThreadConnectorLine" && b.IsVisible);
                var lineOrigin = line.TranslatePoint(new Point(0, 0), window)!.Value;
                var midY = lineOrigin.Y + line.Bounds.Height / 2;
                var probes = AbsoluteProbeXs
                    .Concat(LineRelativeProbeXOffsets.Select(offset => lineOrigin.X + offset))
                    .Select(x => ($"x={x}", new Point(x, midY))).ToArray();

                foreach (var (name, point) in probes)
                {
                    scrollViewer.Offset = default;
                    Dispatcher.UIThread.RunJobs();
                    var hit = window.GetVisualAt(point);
                    // Let the pointer position settle first; a wheel with no settled pointer-over is
                    // dropped by the headless platform.
                    window.MouseMove(point);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    window.MouseWheel(point, new Vector(0, WheelDeltaY));
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(scrollViewer.Offset.Y > 0, $"wheel over '{name}' (hit {hit?.GetType().Name}) did not scroll");
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
