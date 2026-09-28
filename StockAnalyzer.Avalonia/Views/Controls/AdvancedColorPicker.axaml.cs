using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using StockAnalyzer.Core.Models;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

using AvaloniaColor = Avalonia.Media.Color;

namespace StockAnalyzer.Avalonia.Views.Controls;

public partial class AdvancedColorPicker : UserControl
{
    public static readonly StyledProperty<IndicatorColor> ColorProperty =
        AvaloniaProperty.Register<AdvancedColorPicker, IndicatorColor>(
            nameof(Color), 
            defaultValue: IndicatorColor.Gray, 
            defaultBindingMode: BindingMode.TwoWay);

    public IndicatorColor Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private bool _isUpdating = false;
    private static readonly Regex HexRegex = new(@"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    public AdvancedColorPicker()
    {
        InitializeComponent();

        AlphaSlider.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty && !_isUpdating)
            {
                UpdateColorFromUI();
            }
        };

        HexInput.LostFocus += (s, e) => ValidateAndApplyHex();
        HexInput.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ValidateAndApplyHex();
                HexInput.Focus(); // Re-focus to consume Enter
            }
        };

        this.GetObservable(ColorProperty).Subscribe(OnColorChanged);
    }

    private void OnColorChanged(IndicatorColor newColor)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        try
        {
            // Update Preview
            PreviewRectangle.Fill = new SolidColorBrush(AvaloniaColor.FromArgb(newColor.A, newColor.R, newColor.G, newColor.B));

            // Update HEX TextBox
            HexInput.Text = newColor.ToString();

            // Update Alpha UI
            AlphaSlider.Value = newColor.A;
            AlphaText.Text = newColor.A.ToString();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void UpdateColorFromUI()
    {
        _isUpdating = true;
        try
        {
            byte a = (byte)AlphaSlider.Value;
            IndicatorColor current = Color;
            Color = new IndicatorColor(a, current.R, current.G, current.B);
            AlphaText.Text = a.ToString();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void ValidateAndApplyHex()
    {
        string text = HexInput.Text ?? "";
        if (HexRegex.IsMatch(text))
        {
            if (TryParseHex(text, out IndicatorColor parsed))
            {
                // Preserve current alpha if only 6 digits provided? 
                // No, Prompt 80-2 says "HEX Accepts 6 or 8". If 6, we should probably keep current alpha or set to FF.
                // The regex and parse handle both. 
                Color = parsed;
                return;
            }
        }

        // Rollback
        HexInput.Text = Color.ToString();
    }

    private bool TryParseHex(string hex, out IndicatorColor color)
    {
        color = IndicatorColor.Gray;
        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.StartsWith("#")) span = span.Slice(1);

        if (span.Length == 6)
        {
            if (byte.TryParse(span.Slice(0, 2), NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, null, out byte b))
            {
                color = new IndicatorColor(Color.A, r, g, b); // Preserve Alpha when 6 digits entered
                return true;
            }
        }
        else if (span.Length == 8)
        {
            if (byte.TryParse(span.Slice(0, 2), NumberStyles.HexNumber, null, out byte a) &&
                byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(span.Slice(6, 2), NumberStyles.HexNumber, null, out byte b))
            {
                color = new IndicatorColor(a, r, g, b);
                return true;
            }
        }

        return false;
    }
}
