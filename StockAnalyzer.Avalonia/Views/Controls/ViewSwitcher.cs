using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// A ContentControl that caches View instances for each ViewModel to ensure Zero-Object-Recreation.
/// </summary>
public sealed class ViewSwitcher : ContentControl
{
    private readonly Dictionary<object, Control> _cache = new(capacity: 16);

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

            if (newValue is null)
            {
                Content = null;
            }
            else if (newValue is Control control)
            {
                Content = control;
            }
            else
            {
                if (_cache.TryGetValue(newValue, out var cachedView))
                {
                    cachedView.DataContext = newValue;
                    Content = cachedView;
                }
                else
                {
                    var locator = new ViewLocator();
                    var view = locator.Build(newValue);
                    if (view is not null)
                    {
                        view.DataContext = newValue;
                        _cache[newValue] = view;
                        Content = view;
                    }
                    else
                    {
                        Content = null;
                    }
                }
            }
        }
        
        base.OnPropertyChanged(change);
    }

    /// <summary>
    /// Clears the cache for a specific ViewModel, typically when it's being disposed.
    /// </summary>
    public void ClearCache(object viewModel)
    {
        if (_cache.TryGetValue(viewModel, out var view))
        {
            if (view is System.IDisposable disposable)
            {
                disposable.Dispose();
            }
            _cache.Remove(viewModel);
        }
    }
}
