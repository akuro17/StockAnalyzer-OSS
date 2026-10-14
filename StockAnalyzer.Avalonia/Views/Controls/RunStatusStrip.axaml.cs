using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// The run-status strip shared by the Training Wizard, RL and Portfolio RL windows: an optional progress bar (determinate or indeterminate) with its
/// percentage, the phase text and the status message. The host places it between the scrolled body and the footer and binds the styled properties;
/// the strip is visible only while <see cref="ShowProgress"/> is set or <see cref="Message"/> or <see cref="Phase"/> is non-empty (a phase text can carry
/// a failure detail while no run is active), so an idle window shows nothing.
/// </summary>
public partial class RunStatusStrip : UserControl
{
    public static readonly StyledProperty<bool> ShowProgressProperty =
        AvaloniaProperty.Register<RunStatusStrip, bool>(nameof(ShowProgress));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<RunStatusStrip, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<double> PercentProperty =
        AvaloniaProperty.Register<RunStatusStrip, double>(nameof(Percent));

    public static readonly StyledProperty<string?> PercentTextProperty =
        AvaloniaProperty.Register<RunStatusStrip, string?>(nameof(PercentText));

    public static readonly StyledProperty<string?> PhaseProperty =
        AvaloniaProperty.Register<RunStatusStrip, string?>(nameof(Phase));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<RunStatusStrip, string?>(nameof(Message));

    public static readonly StyledProperty<string?> MessageAutomationIdProperty =
        AvaloniaProperty.Register<RunStatusStrip, string?>(nameof(MessageAutomationId));

    public RunStatusStrip()
    {
        InitializeComponent();
        UpdateParts();
    }

    /// <summary>Shows the progress row (bar and percentage).</summary>
    public bool ShowProgress { get => GetValue(ShowProgressProperty); set => SetValue(ShowProgressProperty, value); }

    /// <summary>The bar has no known fraction (the percentage is then hidden unless <see cref="PercentText"/> is given).</summary>
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    /// <summary>The bar value, 0 to 100.</summary>
    public double Percent { get => GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    /// <summary>The percentage label; when null a determinate bar shows <see cref="Percent"/> as a whole number with a percent sign.</summary>
    public string? PercentText { get => GetValue(PercentTextProperty); set => SetValue(PercentTextProperty, value); }

    /// <summary>The current stage or phase, shown above the message.</summary>
    public string? Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    /// <summary>The status or refusal message of the window; empty hides it.</summary>
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    /// <summary>The automation id of the message text, so a window keeps the id its UI tests use.</summary>
    public string? MessageAutomationId { get => GetValue(MessageAutomationIdProperty); set => SetValue(MessageAutomationIdProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowProgressProperty || change.Property == IsIndeterminateProperty || change.Property == PercentProperty
            || change.Property == PercentTextProperty || change.Property == PhaseProperty || change.Property == MessageProperty
            || change.Property == MessageAutomationIdProperty)
            UpdateParts();
    }

    private void UpdateParts()
    {
        if (StripRoot is null) return; // before InitializeComponent finished
        bool hasMessage = !string.IsNullOrEmpty(Message);
        bool hasPhase = !string.IsNullOrEmpty(Phase);
        string? percentLabel = PercentText ?? (IsIndeterminate ? null : string.Create(CultureInfo.CurrentCulture, $"{Percent:0}%"));

        ProgressRow.IsVisible = ShowProgress;
        PercentLabel.Text = percentLabel;
        PercentLabel.IsVisible = !string.IsNullOrEmpty(percentLabel);
        PhaseLabel.Text = Phase;
        PhaseLabel.IsVisible = hasPhase;
        MessageLabel.Text = Message;
        MessageLabel.IsVisible = hasMessage;
        AutomationProperties.SetAutomationId(MessageLabel, MessageAutomationId);
        StripRoot.IsVisible = ShowProgress || hasMessage || hasPhase;
    }
}
