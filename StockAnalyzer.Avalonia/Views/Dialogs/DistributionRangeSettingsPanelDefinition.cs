using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings panel definition for <see cref="DistributionRangeObject"/>.
/// Enforces <c>D-3=A</c> (HorizontalPriceProfile restricted to Price mode) and
/// <c>D-7=A</c> (Student-t DF override gating &amp; [2.1, 1000] / GEV [-0.5, 0.45] bounds).
/// </summary>
public sealed class DistributionRangeSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is DistributionRangeObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var distPanel = dialogWindow.FindControl<StackPanel>("DistributionRangePanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (distPanel != null) distPanel.IsVisible = true;

        var dataModeCombo = dialogWindow.FindControl<ComboBox>("DistDataModeCombo");
        var displayStyleCombo = dialogWindow.FindControl<ComboBox>("DistDisplayStyleCombo");
        var binningRuleCombo = dialogWindow.FindControl<ComboBox>("DistBinningRuleCombo");
        var refDistCombo = dialogWindow.FindControl<ComboBox>("DistReferenceDistCombo");
        var overrideDfCheck = dialogWindow.FindControl<CheckBox>("DistOverrideStudentTCheck");

        if (distPanel != null && distPanel.Tag is not string)
        {
            distPanel.Tag = "wired";
            ApplyDomainBounds(dialogWindow);
            if (dataModeCombo != null)
            {
                dataModeCombo.SelectionChanged += (_, _) => UpdateControlStates(dialogWindow);
            }
            if (displayStyleCombo != null)
            {
                displayStyleCombo.SelectionChanged += (_, _) => UpdateControlStates(dialogWindow);
            }
            if (binningRuleCombo != null)
            {
                binningRuleCombo.SelectionChanged += (_, _) => UpdateControlStates(dialogWindow);
            }
            if (refDistCombo != null)
            {
                refDistCombo.SelectionChanged += (_, _) => UpdateControlStates(dialogWindow);
            }
            if (overrideDfCheck != null)
            {
                overrideDfCheck.IsCheckedChanged += (_, _) => UpdateControlStates(dialogWindow);
            }
        }
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not DistributionRangeObject dist) return;

        var dataModeCombo = dialogWindow.FindControl<ComboBox>("DistDataModeCombo");
        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("DistPriceSourceCombo");
        var displayStyleCombo = dialogWindow.FindControl<ComboBox>("DistDisplayStyleCombo");
        var binningRuleCombo = dialogWindow.FindControl<ComboBox>("DistBinningRuleCombo");
        var fixedBinSpin = dialogWindow.FindControl<NumericUpDown>("DistFixedBinCountSpin");
        var refDistCombo = dialogWindow.FindControl<ComboBox>("DistReferenceDistCombo");
        var overrideDfCheck = dialogWindow.FindControl<CheckBox>("DistOverrideStudentTCheck");
        var studentTDfSpin = dialogWindow.FindControl<NumericUpDown>("DistStudentTDfSpin");
        var gevShapeSpin = dialogWindow.FindControl<NumericUpDown>("DistGevShapeSpin");
        var bwMultSpin = dialogWindow.FindControl<NumericUpDown>("DistBandwidthMultSpin");
        var includeKdeCheck = dialogWindow.FindControl<CheckBox>("DistIncludeKdeCheck");
        var showStatsCheck = dialogWindow.FindControl<CheckBox>("DistShowStatsLabelCheck");
        var plotRatioSpin = dialogWindow.FindControl<NumericUpDown>("DistPlotHeightRatioSpin");
        var barColorPicker = dialogWindow.FindControl<ColorPicker>("DistBarColorPicker");
        var barOpacitySpin = dialogWindow.FindControl<NumericUpDown>("DistBarOpacitySpin");
        var kdeColorPicker = dialogWindow.FindControl<ColorPicker>("DistKdeColorPicker");
        var refColorPicker = dialogWindow.FindControl<ColorPicker>("DistRefColorPicker");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("DistFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("DistFillOpacitySpin");
        var rtlCheck = dialogWindow.FindControl<CheckBox>("DistRightToLeftCheck");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("DistLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("DistRangeBarsSpin");

        if (dataModeCombo != null) dataModeCombo.SelectedIndex = (int)dist.DataMode;
        if (priceSourceCombo != null) priceSourceCombo.SelectedIndex = PriceDataHelper.GetPriceTypeIndex(dist.PriceSource);
        if (displayStyleCombo != null) displayStyleCombo.SelectedIndex = (int)dist.DisplayStyle;
        if (binningRuleCombo != null) binningRuleCombo.SelectedIndex = (int)dist.BinningRule;
        if (fixedBinSpin != null) fixedBinSpin.Value = Math.Clamp(dist.FixedBinCount, DistributionRangeParameters.MinFixedBinCount, DistributionRangeParameters.MaxFixedBinCount);
        if (refDistCombo != null) refDistCombo.SelectedIndex = (int)dist.ReferenceDistribution;
        if (overrideDfCheck != null) overrideDfCheck.IsChecked = dist.OverrideStudentTDegreesOfFreedom;
        if (studentTDfSpin != null) studentTDfSpin.Value = DistributionRangeParameters.ClampStudentTDegreesOfFreedom(dist.StudentTDegreesOfFreedom);
        if (gevShapeSpin != null) gevShapeSpin.Value = DistributionRangeParameters.ClampGevShapeParameter(dist.GevShapeParameter);
        if (bwMultSpin != null) bwMultSpin.Value = IndicatorCalculationHelper.ToDecimalOrNull(Math.Clamp(dist.BandwidthMultiplier, DistributionRangeParameters.MinBandwidthMultiplier, DistributionRangeParameters.MaxBandwidthMultiplier)) ?? IndicatorCalculationHelper.ToDecimalOrNull(DistributionRangeParameters.DefaultBandwidthMultiplier) ?? 1.0m;
        if (includeKdeCheck != null) includeKdeCheck.IsChecked = dist.IncludeKde;
        if (showStatsCheck != null) showStatsCheck.IsChecked = dist.ShowStatsLabel;
        if (plotRatioSpin != null) plotRatioSpin.Value = IndicatorCalculationHelper.ToDecimalOrNull(Math.Clamp(dist.PlotHeightRatio, DistributionRangeObject.MinPlotHeightRatio, DistributionRangeObject.MaxPlotHeightRatio)) ?? IndicatorCalculationHelper.ToDecimalOrNull(DistributionRangeObject.DefaultPlotHeightRatio) ?? 0.45m;
        if (barColorPicker != null) barColorPicker.Color = dist.BarColor;
        if (barOpacitySpin != null) barOpacitySpin.Value = IndicatorCalculationHelper.ToDecimalOrNull(Math.Clamp(dist.BarOpacity * DistributionRangeObject.MaxOpacityPercent, DistributionRangeObject.MinOpacityPercent, DistributionRangeObject.MaxOpacityPercent)) ?? DistributionRangeObject.DefaultBarOpacityPercent;
        if (kdeColorPicker != null) kdeColorPicker.Color = dist.KdeColor;
        if (refColorPicker != null) refColorPicker.Color = dist.ReferenceCurveColor;
        if (fillColorPicker != null) fillColorPicker.Color = dist.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = Math.Clamp(dist.FillOpacity, DistributionRangeObject.MinOpacityPercent, DistributionRangeObject.MaxOpacityPercent);
        if (rtlCheck != null) rtlCheck.IsChecked = dist.RightToLeft;
        if (lockRangeCheck != null) lockRangeCheck.IsChecked = dist.LockRange;
        if (rangeBarsSpin != null && dist.RangeBars > 0) rangeBarsSpin.Value = dist.RangeBars;

        UpdateControlStates(dialogWindow);
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not DistributionRangeObject dist) return;

        var dataModeCombo = dialogWindow.FindControl<ComboBox>("DistDataModeCombo");
        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("DistPriceSourceCombo");
        var displayStyleCombo = dialogWindow.FindControl<ComboBox>("DistDisplayStyleCombo");
        var binningRuleCombo = dialogWindow.FindControl<ComboBox>("DistBinningRuleCombo");
        var fixedBinSpin = dialogWindow.FindControl<NumericUpDown>("DistFixedBinCountSpin");
        var refDistCombo = dialogWindow.FindControl<ComboBox>("DistReferenceDistCombo");
        var overrideDfCheck = dialogWindow.FindControl<CheckBox>("DistOverrideStudentTCheck");
        var studentTDfSpin = dialogWindow.FindControl<NumericUpDown>("DistStudentTDfSpin");
        var gevShapeSpin = dialogWindow.FindControl<NumericUpDown>("DistGevShapeSpin");
        var bwMultSpin = dialogWindow.FindControl<NumericUpDown>("DistBandwidthMultSpin");
        var includeKdeCheck = dialogWindow.FindControl<CheckBox>("DistIncludeKdeCheck");
        var showStatsCheck = dialogWindow.FindControl<CheckBox>("DistShowStatsLabelCheck");
        var plotRatioSpin = dialogWindow.FindControl<NumericUpDown>("DistPlotHeightRatioSpin");
        var barColorPicker = dialogWindow.FindControl<ColorPicker>("DistBarColorPicker");
        var barOpacitySpin = dialogWindow.FindControl<NumericUpDown>("DistBarOpacitySpin");
        var kdeColorPicker = dialogWindow.FindControl<ColorPicker>("DistKdeColorPicker");
        var refColorPicker = dialogWindow.FindControl<ColorPicker>("DistRefColorPicker");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("DistFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("DistFillOpacitySpin");
        var rtlCheck = dialogWindow.FindControl<CheckBox>("DistRightToLeftCheck");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("DistLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("DistRangeBarsSpin");

        if (dataModeCombo != null && dataModeCombo.SelectedIndex >= 0 && Enum.IsDefined(typeof(DistributionDataMode), dataModeCombo.SelectedIndex))
        {
            dist.DataMode = (DistributionDataMode)dataModeCombo.SelectedIndex;
        }
        if (priceSourceCombo != null && priceSourceCombo.SelectedIndex >= 0)
        {
            dist.PriceSource = PriceDataHelper.GetPriceTypeByIndex(priceSourceCombo.SelectedIndex);
        }
        if (displayStyleCombo != null && displayStyleCombo.SelectedIndex >= 0 && Enum.IsDefined(typeof(DistributionDisplayStyle), displayStyleCombo.SelectedIndex))
        {
            dist.DisplayStyle = (DistributionDisplayStyle)displayStyleCombo.SelectedIndex;
        }
        if (binningRuleCombo != null && binningRuleCombo.SelectedIndex >= 0 && Enum.IsDefined(typeof(DistributionBinningRule), binningRuleCombo.SelectedIndex))
        {
            dist.BinningRule = (DistributionBinningRule)binningRuleCombo.SelectedIndex;
        }
        if (fixedBinSpin?.Value != null)
        {
            dist.FixedBinCount = Math.Clamp((int)fixedBinSpin.Value, DistributionRangeParameters.MinFixedBinCount, DistributionRangeParameters.MaxFixedBinCount);
        }
        if (refDistCombo != null && refDistCombo.SelectedIndex >= 0 && Enum.IsDefined(typeof(ReferenceDistributionType), refDistCombo.SelectedIndex))
        {
            dist.ReferenceDistribution = (ReferenceDistributionType)refDistCombo.SelectedIndex;
        }
        if (overrideDfCheck?.IsChecked != null)
        {
            dist.OverrideStudentTDegreesOfFreedom = overrideDfCheck.IsChecked.Value;
        }
        if (studentTDfSpin?.Value is decimal dfValue)
        {
            dist.StudentTDegreesOfFreedom = DistributionRangeParameters.ClampStudentTDegreesOfFreedom(dfValue);
        }
        if (gevShapeSpin?.Value is decimal xiValue)
        {
            dist.GevShapeParameter = DistributionRangeParameters.ClampGevShapeParameter(xiValue);
        }
        if (bwMultSpin?.Value != null)
        {
            dist.BandwidthMultiplier = Math.Clamp((double)bwMultSpin.Value, DistributionRangeParameters.MinBandwidthMultiplier, DistributionRangeParameters.MaxBandwidthMultiplier);
        }
        if (includeKdeCheck?.IsChecked != null) dist.IncludeKde = includeKdeCheck.IsChecked.Value;
        if (showStatsCheck?.IsChecked != null) dist.ShowStatsLabel = showStatsCheck.IsChecked.Value;
        if (plotRatioSpin?.Value != null) dist.PlotHeightRatio = Math.Clamp((double)plotRatioSpin.Value, DistributionRangeObject.MinPlotHeightRatio, DistributionRangeObject.MaxPlotHeightRatio);
        if (barColorPicker != null) dist.BarColor = barColorPicker.Color;
        if (barOpacitySpin?.Value != null) dist.BarOpacity = Math.Clamp((double)barOpacitySpin.Value / DistributionRangeObject.MaxOpacityPercent, 0.0, 1.0);
        if (kdeColorPicker != null) dist.KdeColor = kdeColorPicker.Color;
        if (refColorPicker != null) dist.ReferenceCurveColor = refColorPicker.Color;
        if (fillColorPicker != null) dist.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) dist.FillOpacity = Math.Clamp((int)fillOpacitySpin.Value, DistributionRangeObject.MinOpacityPercent, DistributionRangeObject.MaxOpacityPercent);
        if (rtlCheck?.IsChecked != null) dist.RightToLeft = rtlCheck.IsChecked.Value;
        if (lockRangeCheck?.IsChecked != null) dist.LockRange = lockRangeCheck.IsChecked.Value;
        if (rangeBarsSpin?.Value != null && (int)rangeBarsSpin.Value != dist.RangeBars)
        {
            dist.RangeBars = (int)rangeBarsSpin.Value;
        }
    }

    internal static void UpdateControlStates(Window dialogWindow)
    {
        var dataModeCombo = dialogWindow.FindControl<ComboBox>("DistDataModeCombo");
        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("DistPriceSourceCombo");
        var displayStyleCombo = dialogWindow.FindControl<ComboBox>("DistDisplayStyleCombo");
        var horizontalItem = dialogWindow.FindControl<ComboBoxItem>("DistHorizontalProfileItem");
        var styleHintText = dialogWindow.FindControl<TextBlock>("DistDisplayStyleHintText");
        var binningRuleCombo = dialogWindow.FindControl<ComboBox>("DistBinningRuleCombo");
        var fixedBinSpin = dialogWindow.FindControl<NumericUpDown>("DistFixedBinCountSpin");
        var refDistCombo = dialogWindow.FindControl<ComboBox>("DistReferenceDistCombo");
        var overrideDfCheck = dialogWindow.FindControl<CheckBox>("DistOverrideStudentTCheck");
        var studentTDfSpin = dialogWindow.FindControl<NumericUpDown>("DistStudentTDfSpin");
        var gevShapeSpin = dialogWindow.FindControl<NumericUpDown>("DistGevShapeSpin");
        var rtlCheck = dialogWindow.FindControl<CheckBox>("DistRightToLeftCheck");

        bool isPriceMode = dataModeCombo?.SelectedIndex == (int)DistributionDataMode.Price;
        bool isHorizontalStyle = displayStyleCombo?.SelectedIndex == (int)DistributionDisplayStyle.HorizontalPriceProfile;

        if (priceSourceCombo != null) priceSourceCombo.IsEnabled = isPriceMode;
        if (horizontalItem != null) horizontalItem.IsEnabled = isPriceMode;
        if (styleHintText != null) styleHintText.IsVisible = !isPriceMode && (isHorizontalStyle || horizontalItem != null);
        if (rtlCheck != null) rtlCheck.IsEnabled = isHorizontalStyle && isPriceMode;

        if (fixedBinSpin != null)
        {
            fixedBinSpin.IsEnabled = binningRuleCombo?.SelectedIndex == (int)DistributionBinningRule.FixedCount;
        }

        int refIdx = refDistCombo?.SelectedIndex ?? -1;
        bool isStudentT = refIdx == (int)ReferenceDistributionType.StudentT;
        bool isGev = refIdx == (int)ReferenceDistributionType.Gev;
        bool overrideDf = overrideDfCheck?.IsChecked == true;

        if (overrideDfCheck != null) overrideDfCheck.IsEnabled = isStudentT;
        if (studentTDfSpin != null) studentTDfSpin.IsEnabled = isStudentT && overrideDf;
        if (gevShapeSpin != null) gevShapeSpin.IsEnabled = isGev;
    }

    private static void ApplyDomainBounds(Window dialogWindow)
    {
        ConfigureDecimalSpin(dialogWindow.FindControl<NumericUpDown>("DistFixedBinCountSpin"), DistributionRangeParameters.MinFixedBinCount, DistributionRangeParameters.MaxFixedBinCount);
        ConfigureDecimalSpin(dialogWindow.FindControl<NumericUpDown>("DistStudentTDfSpin"), DistributionRangeParameters.MinStudentTDegreesOfFreedom, DistributionRangeParameters.MaxStudentTDegreesOfFreedom);
        ConfigureDecimalSpin(dialogWindow.FindControl<NumericUpDown>("DistGevShapeSpin"), DistributionRangeParameters.MinGevShapeParameter, DistributionRangeParameters.MaxGevShapeParameter);
        ConfigureDecimalSpin(
            dialogWindow.FindControl<NumericUpDown>("DistBandwidthMultSpin"),
            IndicatorCalculationHelper.ToDecimalOrNull(DistributionRangeParameters.MinBandwidthMultiplier) ?? 0.25m,
            IndicatorCalculationHelper.ToDecimalOrNull(DistributionRangeParameters.MaxBandwidthMultiplier) ?? 4.0m);
        ConfigureDecimalSpin(
            dialogWindow.FindControl<NumericUpDown>("DistPlotHeightRatioSpin"),
            IndicatorCalculationHelper.ToDecimalOrNull(DistributionRangeObject.MinPlotHeightRatio) ?? 0.15m,
            IndicatorCalculationHelper.ToDecimalOrNull(DistributionRangeObject.MaxPlotHeightRatio) ?? 0.90m);
        ConfigureDecimalSpin(dialogWindow.FindControl<NumericUpDown>("DistBarOpacitySpin"), DistributionRangeObject.MinOpacityPercent, DistributionRangeObject.MaxOpacityPercent);
        ConfigureDecimalSpin(dialogWindow.FindControl<NumericUpDown>("DistFillOpacitySpin"), DistributionRangeObject.MinOpacityPercent, DistributionRangeObject.MaxOpacityPercent);
    }

    private static void ConfigureDecimalSpin(NumericUpDown? spin, decimal min, decimal max)
    {
        if (spin == null) return;
        spin.Minimum = min;
        spin.Maximum = max;
    }
}
