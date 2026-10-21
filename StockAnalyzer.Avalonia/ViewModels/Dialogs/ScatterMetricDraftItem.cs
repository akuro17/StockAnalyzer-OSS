using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// One scatter metric under edit in the registration window: the definition, and the live editors of its parts (output series, the indicator's parameter
/// object, time frame, Offset). Every edit changes the definition at once - and so its <see cref="Label"/> - and raises <see cref="Edited"/>; nothing is saved here
/// (the window saves the whole draft when the user presses OK).
/// </summary>
public sealed class ScatterMetricDraftItem : ObservableObject, IDisposable
{
    private readonly CoreIndicatorSettings? _settings;
    private readonly int _maxOffset;
    private readonly string? _fixedTitle;

    /// <param name="definition">The definition edited; owned by this item.</param>
    /// <param name="settings">The indicator settings whose <see cref="CoreIndicatorSettings.ParameterObject"/> the user edits; null for a Column or a price.</param>
    /// <param name="outputs">The output series the user can choose from.</param>
    /// <param name="maxOffset">The largest Offset the user can enter (<c>ScatterMetrics:MaxOffset</c>).</param>
    /// <param name="fixedTitle">A title that does not follow the label (the catalog name of a metric that is not added yet); null = the label.</param>
    public ScatterMetricDraftItem(ScatterMetricDefinition definition, CoreIndicatorSettings? settings, IReadOnlyList<string> outputs, int maxOffset, string? fixedTitle = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
        _settings = settings;
        _maxOffset = maxOffset;
        _fixedTitle = fixedTitle;

        if (ParameterObject is { } parameters)
        {
            parameters.PropertyChanged += OnParameterChanged;
        }
    }

    /// <summary>The definition in its current state.</summary>
    public ScatterMetricDefinition Definition { get; }

    /// <summary>The text of the metric in lists and on the axes; it follows every edit.</summary>
    public string Label => Definition.Label;

    /// <summary>The heading of the detail column.</summary>
    public string Title => _fixedTitle ?? Label;

    public IReadOnlyList<string> Outputs { get; }

    public bool HasMultipleOutputs => Outputs.Count > 1;

    /// <summary>The live parameter object the settings view edits; null when the metric has no parameters.</summary>
    public CoreIndicatorParameterBase? ParameterObject => _settings?.ParameterObject;

    public bool HasParameters => ParameterObject != null;

    public string SelectedOutput
    {
        get => Definition.Source.OutputName;
        set
        {
            if (value is null || string.Equals(value, Definition.Source.OutputName, StringComparison.Ordinal))
            {
                return;
            }

            Definition.Source.OutputName = value;
            OnPropertyChanged();
            RaiseEdited();
        }
    }

    /// <summary>
    /// The reason the time frame cannot be chosen (a fundamental Column is read from the ticker, not from a bar, so it stays on Day), already localized.
    /// Set by the window's view model when it creates the item.
    /// </summary>
    public string? FixedTimeFrameReason { get; init; }

    /// <summary>True when the time frame can be chosen: an Indicator, a Price source or a Column that names a bar value (<see cref="ScatterMetricEligibility.AllowsTimeFrameChoice"/>).</summary>
    public bool IsTimeFrameEditable => ScatterMetricEligibility.AllowsTimeFrameChoice(Definition.Source);

    /// <summary>The tooltip of the time frame box: null while it can be chosen, otherwise the reason it cannot (shown on the disabled box).</summary>
    public string? TimeFrameToolTip => IsTimeFrameEditable ? null : FixedTimeFrameReason;

    /// <summary>The time frame the metric is read on. A time frame that cannot be chosen, is not supported or is the current one changes nothing.</summary>
    public TimeFrame TimeFrame
    {
        get => Definition.Source.TimeFrame;
        set
        {
            if (!IsTimeFrameEditable || !ScatterMetricTimeFrames.IsSupported(value) || value == Definition.Source.TimeFrame)
            {
                // The box may show a value that was refused; make it show the stored one again.
                OnPropertyChanged();
                return;
            }

            Definition.Source.TimeFrame = value;
            OnPropertyChanged();
            RaiseEdited();
        }
    }

    /// <summary>Bars of the metric's time frame before the ticker's latest bar; always a whole number between 0 and the configured maximum.</summary>
    public decimal Offset
    {
        get => Definition.Source.Offset;
        set
        {
            int offset = (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0m, _maxOffset);
            if (offset == Definition.Source.Offset)
            {
                // The box may show a value outside the range; make it show the stored one again.
                OnPropertyChanged();
                return;
            }

            Definition.Source.Offset = offset;
            OnPropertyChanged();
            RaiseEdited();
        }
    }

    private bool _isSelected;

    /// <summary>True while this row is the one the detail column edits (the list highlights it).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Raised after every change of the definition.</summary>
    public event EventHandler? Edited;

    public void Dispose()
    {
        if (ParameterObject is { } parameters)
        {
            parameters.PropertyChanged -= OnParameterChanged;
        }
    }

    /// <summary>Copies the stored <paramref name="values"/> onto the editable properties of <paramref name="target"/> (a fresh parameter object of the indicator).</summary>
    public static void ApplyParameters(CoreIndicatorParameterBase target, IReadOnlyDictionary<string, object> values)
    {
        foreach (var property in target.GetType().GetProperties())
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0 || property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            object? stored = null;
            foreach (KeyValuePair<string, object> pair in values)
            {
                if (string.Equals(pair.Key, property.Name, StringComparison.OrdinalIgnoreCase))
                {
                    stored = pair.Value;
                    break;
                }
            }

            if (stored is null)
            {
                continue;
            }

            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            try
            {
                object converted = type.IsInstanceOfType(stored)
                    ? stored
                    : type.IsEnum && stored is IConvertible number
                        ? Enum.ToObject(type, number.ToInt64(CultureInfo.InvariantCulture))
                        : Convert.ChangeType(stored, type, CultureInfo.InvariantCulture);
                property.SetValue(target, converted);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException or System.Reflection.TargetInvocationException)
            {
                // A stored value the parameter object no longer accepts keeps the object's own default.
            }
        }
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ParameterObject is null)
        {
            return;
        }

        // The same dictionary the Screener stores for a registered condition.
        Definition.Source.Parameters.Clear();
        foreach (KeyValuePair<string, object> pair in IndicatorRegistrationViewModel.BuildParameterDictionary(ParameterObject))
        {
            Definition.Source.Parameters[pair.Key] = pair.Value;
        }

        RaiseEdited();
    }

    private void RaiseEdited()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Title));
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
