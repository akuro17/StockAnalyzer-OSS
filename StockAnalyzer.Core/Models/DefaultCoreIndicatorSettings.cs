using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models;

public static class DefaultCoreIndicatorSettings
{
    public static List<CoreIndicatorSettings> GetDefault()
    {
        return new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.SMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.EMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreEmaParameter { Period = IndicatorDefaultConstants.EmaPeriod }, 
                Color = IndicatorDefaultConstants.EmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.BB, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreBollingerBandsParameter { Period = IndicatorDefaultConstants.BollingerPeriod, StdDevMultiplier = IndicatorDefaultConstants.BollingerStdDevMultiplier }, 
                Color = IndicatorDefaultConstants.BollingerMiddleColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "Middle", DisplayName = "Middle Band", Color = IndicatorDefaultConstants.BollingerMiddleColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Bands", DisplayName = "Bands (Upper/Lower)", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { "Upper", "Lower" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.Ichimoku, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreIchimokuParameter(), 
                Color = IndicatorDefaultConstants.IchimokuBaseColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                 SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "Tenkan", DisplayName = "Tenkan-sen", Color = IndicatorDefaultConstants.IchimokuTenkanColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Kijun", DisplayName = "Kijun-sen", Color = IndicatorDefaultConstants.IchimokuKijunColor, TargetSeries = new List<string> { "KijunSen" } },
                    new SeriesColorConfig { Name = "Chikou", DisplayName = "Chikou Span", Color = IndicatorDefaultConstants.IchimokuChikouColor, TargetSeries = new List<string> { "ChikouSpan" } },
                    new SeriesColorConfig { Name = "SenkouA", DisplayName = "Senkou Span A", Color = IndicatorDefaultConstants.IchimokuSenkouAColor, TargetSeries = new List<string> { "SenkouSpanA" } },
                    new SeriesColorConfig { Name = "SenkouB", DisplayName = "Senkou Span B", Color = IndicatorDefaultConstants.IchimokuSenkouBColor, TargetSeries = new List<string> { "SenkouSpanB" } },
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ParabolicSAR, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreParabolicSarParameter { AccelerationStart = IndicatorDefaultConstants.ParabolicAccelerationStart, AccelerationStep = IndicatorDefaultConstants.ParabolicAccelerationStep, AccelerationMax = IndicatorDefaultConstants.ParabolicAccelerationMax }, 
                Color = IndicatorDefaultConstants.ParabolicColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Dot,
                IsOverlay = true,
                UseUpDownColors = true,
                UpColor = IndicatorDefaultConstants.ParabolicUpColor,
                DownColor = IndicatorDefaultConstants.ParabolicDownColor
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.RSI, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreRsiParameter { Period = IndicatorDefaultConstants.RsiPeriod }, 
                Color = IndicatorDefaultConstants.RsiColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.RsiMinValue,
                MaxValue = IndicatorDefaultConstants.RsiMaxValue
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MACD, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreMacdParameter(), 
                Color = IndicatorDefaultConstants.MacdLineColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                UpColor = IndicatorDefaultConstants.MacdHistogramUpColor,
                DownColor = IndicatorDefaultConstants.MacdHistogramDownColor,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "MACD", DisplayName = "MACD Line", Color = IndicatorDefaultConstants.MacdLineColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Signal", DisplayName = "Signal Line", Color = IndicatorDefaultConstants.MacdSignalColor, TargetSeries = new List<string> { "Signal" } },
                    new SeriesColorConfig { Name = "Histogram", DisplayName = "Histogram", Color = IndicatorDefaultConstants.MacdHistogramColor, TargetSeries = new List<string> { "Histogram" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.Stoch, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreStochasticParameter(), 
                Color = IndicatorDefaultConstants.StochBaseColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.StochMinValue,
                MaxValue = IndicatorDefaultConstants.StochMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "K", DisplayName = "%K", Color = IndicatorDefaultConstants.StochKColor, TargetSeries = new List<string> { "Main", "Slow %K" } },
                    new SeriesColorConfig { Name = "D", DisplayName = "%D", Color = IndicatorDefaultConstants.StochDColor, TargetSeries = new List<string> { "PercentD", "Slow %D" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VolumeProfile, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                ParameterObject = new CoreVolumeProfileParameter { Period = IndicatorDefaultConstants.VolumeProfilePeriod, RowCount = IndicatorDefaultConstants.VolumeProfileRowCount, Opacity = IndicatorDefaultConstants.VolumeProfileOpacity, Side = DisplaySide.Left, Mode = VolumeDistributionMode.Proportional }, 
                Color = IndicatorDefaultConstants.VolumeProfileColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MESA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreMesaParameter(), 
                Color = IndicatorDefaultConstants.MesaBaseColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "MAMA", DisplayName = "MAMA", Color = IndicatorDefaultConstants.MesaMamaColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "FAMA", DisplayName = "FAMA", Color = IndicatorDefaultConstants.MesaFamaColor, TargetSeries = new List<string> { "Fama" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.Volume, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                ParameterObject = null, 
                Color = IndicatorDefaultConstants.Gray, // Gray fallback
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                UseUpDownColors = true,
                UpColor = IndicatorDefaultConstants.VolumeUpColor,
                DownColor = IndicatorDefaultConstants.VolumeDownColor
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.PVT, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VPT, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.OBV, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                Color = IndicatorDefaultConstants.Cyan, // Cyan #00ACC1
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MarketStructureShift, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreMarketStructureShiftParameter(), 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Dash,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "BOS", DisplayName = "BOS Lines", Color = IndicatorDefaultConstants.LightBlue, TargetSeries = new List<string> { "BOS_Lines" } },
                    new SeriesColorConfig { Name = "CHoCH", DisplayName = "CHoCH Lines", Color = IndicatorDefaultConstants.Orange, TargetSeries = new List<string> { "CHoCH_Lines" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.StructuralDtw, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreStructuralDtwParameter(), 
                Color = IndicatorDefaultConstants.StructuralDtwColor, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.GranvilleLaw, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreGranvilleLawParameter(), 
                Color = IndicatorDefaultConstants.GranvilleBuy1Color, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = -4,
                MaxValue = 4,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "Buy1", DisplayName = "Buy 1 (Trend Reversal) : MA Flat/Up & Price crosses above MA", Color = IndicatorDefaultConstants.GranvilleBuy1Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Buy2", DisplayName = "Buy 2 (Trend Continuation) : MA Up & Price drops below MA temporarily", Color = IndicatorDefaultConstants.GranvilleBuy2Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Buy3", DisplayName = "Buy 3 (Support Bounce) : MA Up & Price bounces off MA", Color = IndicatorDefaultConstants.GranvilleBuy3Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Buy4", DisplayName = "Buy 4 (Mean Reversion) : MA Down & Price deviates far below MA", Color = IndicatorDefaultConstants.GranvilleBuy4Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell1", DisplayName = "Sell 1 (Trend Reversal) : MA Flat/Down & Price crosses below MA", Color = IndicatorDefaultConstants.GranvilleSell1Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell2", DisplayName = "Sell 2 (Trend Continuation) : MA Down & Price rises above MA temporarily", Color = IndicatorDefaultConstants.GranvilleSell2Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell3", DisplayName = "Sell 3 (Resistance Rejection) : MA Down & Price rejected at MA", Color = IndicatorDefaultConstants.GranvilleSell3Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell4", DisplayName = "Sell 4 (Mean Reversion) : MA Up & Price deviates far above MA", Color = IndicatorDefaultConstants.GranvilleSell4Color, TargetSeries = new List<string> { "Signals" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.WMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.HMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.TMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.DEMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.TEMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VWMA, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
             new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VAMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.SMMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ZLEMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.LSMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ALMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreAlmaParameter(), 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MtfLaggedEma, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreMtfLaggedEmaParameter(), 
                Color = IndicatorDefaultConstants.EmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ATR, 
                Category = CoreIndicatorCategory.Volatility, 
                IsEnabled = false, 
                ParameterObject = new CoreAtrParameter(), 
                Color = IndicatorDefaultConstants.AtrColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ROC, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreRocParameter(), 
                Color = IndicatorDefaultConstants.RocColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.HighLowBand, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = 20 }, 
                Color = IndicatorDefaultConstants.Blue, // Blue
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "High", DisplayName = "High Band", Color = IndicatorDefaultConstants.DeepPurple, TargetSeries = new List<string> { "High" } },
                    new SeriesColorConfig { Name = "Mid", DisplayName = "Middle Band", Color = IndicatorDefaultConstants.Blue, TargetSeries = new List<string> { "Mid" } },
                    new SeriesColorConfig { Name = "Low", DisplayName = "Low Band", Color = IndicatorDefaultConstants.DeepPurple, TargetSeries = new List<string> { "Low" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.DPO, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = 20 }, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.DeMarker, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = 14 }, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            }
        };
    }

    /// <summary>
    /// Auto-heals an indicator that was loaded from an older layout version,
    /// ensuring it has the correct default SeriesColors if missing.
    /// </summary>
    public static void AutoHeal(CoreIndicatorSettings indicator)
    {
        if (indicator.SeriesColors == null || indicator.SeriesColors.Count == 0)
        {
            var defaultSettings = GetDefault().FirstOrDefault(s => s.TypeEnum == indicator.TypeEnum);
            if (defaultSettings != null && defaultSettings.SeriesColors != null && defaultSettings.SeriesColors.Count > 0)
            {
                indicator.SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>();
                foreach (var sc in defaultSettings.SeriesColors)
                {
                    indicator.SeriesColors.Add(sc.Duplicate());
                }
            }
        }
    }
}
