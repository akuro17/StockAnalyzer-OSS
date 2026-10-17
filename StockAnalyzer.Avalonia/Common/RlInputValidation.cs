using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Validate the editor text, rather than trusting NumericUpDown's retained last value.</summary>
public sealed class RlInputValidation : AvaloniaObject
{
    public static readonly AttachedProperty<string?> FieldProperty =
        AvaloniaProperty.RegisterAttached<RlInputValidation, NumericUpDown, string?>("Field");
    public static string? GetField(NumericUpDown control) => control.GetValue(FieldProperty);
    public static void SetField(NumericUpDown control, string? value) => control.SetValue(FieldProperty, value);
    static RlInputValidation()
    {
        FieldProperty.Changed.AddClassHandler<NumericUpDown>((control, _) =>
        {
            control.PropertyChanged -= Changed;
            control.PropertyChanged += Changed;
            control.TemplateApplied -= Applied;
            control.TemplateApplied += Applied;
            Validate(control);
        });
    }
    private static void Applied(object? sender, TemplateAppliedEventArgs args)
    {
        if (sender is not NumericUpDown control) return;
        if (args.NameScope.Find<TextBox>("PART_TextBox") is { } editor)
        {
            AutomationProperties.SetName(editor, AutomationProperties.GetName(control));
            string? id = AutomationProperties.GetAutomationId(control);
            if (!string.IsNullOrWhiteSpace(id)) AutomationProperties.SetAutomationId(editor, id + RlUiConstants.NumericEditorSuffix);
        }
        Validate(control);
    }
    private static void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (sender is NumericUpDown named && args.Property == AutomationProperties.NameProperty)
        {
            var editor = named.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (editor is not null) AutomationProperties.SetName(editor, AutomationProperties.GetName(named));
        }
        if (sender is NumericUpDown control && (args.Property == NumericUpDown.TextProperty
            || args.Property == NumericUpDown.ValueProperty || args.Property == StyledElement.DataContextProperty)) Validate(control);
    }
    private static void Validate(NumericUpDown control)
    {
        if (GetField(control) is not { } field) return;
        Action<string, bool>? report = control.DataContext switch
        {
            RlTrainingDialogViewModel training => training.SetEditorValidity,
            RlPlaybackViewModel playback => playback.SetEditorValidity,
            RlFreeReplayViewModel free => free.SetEditorValidity,
            _ => null
        };
        if (report is null) return;
        bool nullable = field is nameof(RlTrainingDialogViewModel.StorageNonactiveDays) or nameof(RlTrainingDialogViewModel.StorageKeepCount);
        bool integer = field is nameof(RlTrainingDialogViewModel.StorageFailedRetentionDays) or nameof(RlTrainingDialogViewModel.StorageNonactiveDays)
            or nameof(RlTrainingDialogViewModel.StorageKeepCount) or nameof(RlTrainingDialogViewModel.StorageCheckpointKeepCount) or nameof(RlPlaybackViewModel.Decision)
            or nameof(RlFreeReplayViewModel.Decision);
        bool valid = nullable && string.IsNullOrWhiteSpace(control.Text) && control.Value is null;
        if (decimal.TryParse(control.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value))
            valid = control.Value == value && (!integer || value == decimal.Truncate(value));
        report(field, valid);
    }
}
