using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>sa_improve (constraint check N3): the toast overlay that six views used to copy is one control now.</summary>
public class ToastOverlayTests
{
    private const string Message = "toast text";
    private const string Icon = "✅";

    private sealed class HostContext
    {
        public IToastNotificationService? ToastService { get; init; }
    }

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBlock? Find(ToastOverlay overlay, string text) =>
        overlay.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == text);

    [AvaloniaFact]
    public void NullService_KeepsTheOverlayHidden()
    {
        var overlay = new ToastOverlay { Icon = Icon };
        var window = new Window { Content = overlay, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Pump();

            Assert.All(overlay.GetVisualDescendants().OfType<TextBlock>(), t => Assert.False(t.IsEffectivelyVisible));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ShownMessage_IsVisible_AndTheIconOnlyWhenSet()
    {
        var service = new ToastNotificationService();
        var overlay = new ToastOverlay { Service = service, Icon = Icon };
        var window = new Window { Content = overlay, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Pump();
            Assert.Null(Find(overlay, Message)?.Text); // nothing shown yet

            service.ShowNotification(Message);
            Pump();

            var text = Find(overlay, Message);
            Assert.NotNull(text);
            Assert.True(text!.IsEffectivelyVisible);
            Assert.True(Find(overlay, Icon)!.IsEffectivelyVisible);

            // The icon is the first TextBlock of the overlay; it disappears with its glyph, the message stays.
            var iconBlock = overlay.GetVisualDescendants().OfType<TextBlock>().First();
            overlay.Icon = null;
            Pump();
            Assert.False(iconBlock.IsEffectivelyVisible);
            Assert.True(text.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ServiceBoundFromTheHostDataContext_ShowsTheMessage()
    {
        var service = new ToastNotificationService();
        var overlay = new ToastOverlay();
        overlay.Bind(ToastOverlay.ServiceProperty, new Binding(nameof(HostContext.ToastService)));
        var window = new Window { Content = overlay, DataContext = new HostContext { ToastService = service }, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Pump();

            service.ShowNotification(Message);
            Pump();

            Assert.True(Find(overlay, Message)!.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
