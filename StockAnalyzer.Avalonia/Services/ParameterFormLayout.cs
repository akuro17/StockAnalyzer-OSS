namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Specifies the layout style used by <see cref="ParameterViewBuilder"/> when generating UI for indicator parameters.
/// </summary>
public enum ParameterFormLayout
{
    /// <summary>
    /// Default single-column inline layout (260px shaded label column on left, input control on right).
    /// Used by IndicatorPropertiesDialog and other single-column hosts.
    /// </summary>
    InlineLabel = 0,

    /// <summary>
    /// Responsive multi-column field layout (label stacked above input control inside a ResponsiveFormPanel).
    /// Used by IndicatorSettingsWindow Active mode.
    /// </summary>
    TwoColumnFields = 1
}
