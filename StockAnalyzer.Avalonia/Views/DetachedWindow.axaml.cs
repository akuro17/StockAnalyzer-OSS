using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.Views;

public partial class DetachedWindow : Window
{
    public DetachedWindow() : this(null)
    {
    }

    public DetachedWindow(IMessenger? messenger)
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        // Register for closure requests from TearOffService 
        var m = messenger ?? App.Current.Services?.GetService<IMessenger>();
        if (m != null)
        {
            m.Register<DetachedWindow, CloseDetachedWindowMessage>(this, (r, msg) => 
            {
                if (r.DataContext is ViewModels.DetachedWindowViewModel vm && vm.ContainerId == msg.Value)
                {
                    r.Close();
                }
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        
        // Ensure ViewModel cleanup is triggered
        if (DataContext is IDisposable d)
        {
            d.Dispose();
        }

        // Cleanup messenger registrations for this instance
        var messenger = App.Current.Services?.GetService<IMessenger>();
        messenger?.UnregisterAll(this);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ShouldBeginWindowMove(e.Source as Visual)) return;

        BeginMoveDrag(e);
    }

    /// <summary>
    /// A press that started on a tab belongs to the tab (select / drag-reorder) and must not move the window.
    /// Presses on the empty header area still move the window.
    /// </summary>
    internal static bool ShouldBeginWindowMove(Visual? source)
    {
        return source?.FindAncestorOfType<TabItem>(includeSelf: true) == null;
    }
}
