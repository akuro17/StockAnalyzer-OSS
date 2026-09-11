using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the per-object Drawing Settings Dialog.
/// Manages property snapshotting, rollback on cancel, change tracking, and dialog commands.
/// Distinct from global DrawingSettingsViewModel (which configures chart-wide drawing defaults).
/// </summary>
public partial class DrawingObjectSettingsViewModel : ObservableObject
{
    private readonly Action<IChartObject>? _onApply;
    private readonly Dictionary<string, object?> _snapshot = new();
    private readonly List<PropertyInfo> _trackableProperties = new();

    public IChartObject Drawing { get; }

    public string Title => DrawingObjectDisplayNameHelper.GetDisplayName(Drawing);

    [ObservableProperty]
    private IReadOnlyCollection<string> _hiddenParameterTags = Array.Empty<string>();

    [ObservableProperty]
    private bool _isModified;

    public event Action<DrawingSettingsResult>? CloseRequested;

    public DrawingObjectSettingsViewModel(IChartObject drawing, Action<IChartObject>? onApply = null)
    {
        Drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));
        _onApply = onApply;

        InitializeTrackableProperties();
        TakeSnapshot();
    }

    private void InitializeTrackableProperties()
    {
        var type = Drawing.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead && p.CanWrite);

        foreach (var p in props)
        {
            if (DrawingParameterTags.IgnoredPropertyNames.Contains(p.Name))
            {
                continue;
            }
            _trackableProperties.Add(p);
        }
    }

    public void TakeSnapshot()
    {
        _snapshot.Clear();
        foreach (var prop in _trackableProperties)
        {
            try
            {
                var val = prop.GetValue(Drawing);
                _snapshot[prop.Name] = CloneValue(val);
            }
            catch { }
        }
        UpdateIsModified();
    }

    public void Rollback()
    {
        foreach (var prop in _trackableProperties)
        {
            if (_snapshot.TryGetValue(prop.Name, out var originalValue))
            {
                try
                {
                    prop.SetValue(Drawing, CloneValue(originalValue));
                }
                catch { }
            }
        }
        UpdateIsModified();
    }

    public void UpdateIsModified()
    {
        bool modified = false;
        foreach (var prop in _trackableProperties)
        {
            if (_snapshot.TryGetValue(prop.Name, out var originalValue))
            {
                var currentValue = prop.GetValue(Drawing);
                if (!AreValuesEqual(originalValue, currentValue))
                {
                    modified = true;
                    break;
                }
            }
        }
        IsModified = modified;
    }

    private static object? CloneValue(object? val)
    {
        if (val == null) return null;

        var type = val.GetType();
        if (type.IsValueType || type == typeof(string))
        {
            return val;
        }

        if (val is ICloneable cloneable)
        {
            return cloneable.Clone();
        }

        if (val is Array arr)
        {
            var copy = Array.CreateInstance(type.GetElementType()!, arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                copy.SetValue(CloneValue(arr.GetValue(i)), i);
            }
            return copy;
        }

        if (val is System.Collections.IList list)
        {
            try
            {
                if (Activator.CreateInstance(type) is System.Collections.IList newList)
                {
                    foreach (var item in list)
                    {
                        newList.Add(CloneValue(item));
                    }
                    return newList;
                }
            }
            catch { }
        }

        return val;
    }

    private static bool AreValuesEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;

        if (a is Array arrA && b is Array arrB)
        {
            if (arrA.Length != arrB.Length) return false;
            for (int i = 0; i < arrA.Length; i++)
            {
                if (!AreValuesEqual(arrA.GetValue(i), arrB.GetValue(i))) return false;
            }
            return true;
        }

        if (a is System.Collections.IList listA && b is System.Collections.IList listB)
        {
            if (listA.Count != listB.Count) return false;
            for (int i = 0; i < listA.Count; i++)
            {
                if (!AreValuesEqual(listA[i], listB[i])) return false;
            }
            return true;
        }

        return Equals(a, b);
    }

    [RelayCommand]
    private void Apply()
    {
        UpdateIsModified();
        TakeSnapshot();
        _onApply?.Invoke(Drawing);
    }

    [RelayCommand]
    private void Ok()
    {
        UpdateIsModified();
        CloseRequested?.Invoke(DrawingSettingsResult.Changed);
    }

    [RelayCommand]
    private void Cancel()
    {
        Rollback();
        CloseRequested?.Invoke(DrawingSettingsResult.None);
    }

    [RelayCommand]
    private void Delete()
    {
        CloseRequested?.Invoke(DrawingSettingsResult.Deleted);
    }
}
