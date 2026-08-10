using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// A Panel that hosts one materialized View per ViewModel simultaneously (Zero-Object-Recreation)
/// and toggles IsVisible on switch instead of reassigning Content. Reassigning Content on every
/// switch requires detaching and reattaching Visuals, which is prone to Avalonia's
/// "Visual already has a parent" compositor error when a switch happens before a prior
/// attach/detach has fully settled. Keeping every view permanently attached and only toggling
/// visibility avoids that class of failure entirely.
/// </summary>
public sealed class ViewSwitcher : Panel, IRecipient<WorkspaceViewItemRemovedMessage>
{
    private readonly Dictionary<object, ContentControl> _cache = new(capacity: 16);

    public ViewSwitcher()
    {
        AttachedToVisualTree += (_, _) => WeakReferenceMessenger.Default.RegisterAll(this);
        DetachedFromVisualTree += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    /// <summary>
    /// Evicts a removed tab's ViewModel from the cache so its (now-disposed) view is not
    /// held for the lifetime of this panel. See <see cref="WorkspaceViewItemRemovedMessage"/>.
    /// </summary>
    public void Receive(WorkspaceViewItemRemovedMessage message) => ClearCache(message.Value);

    public static readonly StyledProperty<object?> ViewModelProperty =
        AvaloniaProperty.Register<ViewSwitcher, object?>(nameof(ViewModel));

    public object? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ViewModelProperty)
        {
            var newValue = change.NewValue;
            ContentControl? activeWrapper = null;

            if (newValue is not null)
            {
                if (!_cache.TryGetValue(newValue, out var wrapper))
                {
                    Control? content = newValue as Control;
                    if (content == null)
                    {
                        content = new ViewLocator().Build(newValue) as Control;
                        if (content != null)
                        {
                            content.DataContext = newValue;
                        }
                    }

                    if (content != null)
                    {
                        // Wrap in a ContentControl instead of adding the view directly to
                        // this Panel: ContentControl.Content assignment is routed through
                        // ContentPresenter, which performs template/class-handler wiring
                        // that plain Panel.Children.Add bypasses. Without it, hosted
                        // controls (ToggleSwitch, Button, ToggleButton) receive Pointer
                        // events but their own class handlers never run, so they never
                        // capture the pointer to themselves and Click/IsCheckedChanged
                        // never fire even though the events route correctly.
                        wrapper = new ContentControl { Content = content };
                        _cache[newValue] = wrapper;
                        Children.Add(wrapper);
                    }
                }

                activeWrapper = wrapper;
            }

            foreach (var child in Children)
            {
                child.IsVisible = ReferenceEquals(child, activeWrapper);
            }
        }

        base.OnPropertyChanged(change);
    }

    /// <summary>
    /// Clears the cache for a specific ViewModel, typically when it's being disposed.
    /// </summary>
    public void ClearCache(object viewModel)
    {
        if (_cache.TryGetValue(viewModel, out var wrapper))
        {
            Children.Remove(wrapper);
            if (wrapper.Content is System.IDisposable disposable)
            {
                disposable.Dispose();
            }
            _cache.Remove(viewModel);
        }
    }
}
