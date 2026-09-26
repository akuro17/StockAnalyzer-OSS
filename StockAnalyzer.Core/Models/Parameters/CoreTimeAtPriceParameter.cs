using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreTimeAtPriceParameter : CoreIndicatorParameterBase
    {
        private int _period = 0;
        [DisplayName("Period")]
        [Description("Lookback bars, counted backward from the end of the visible range (0 = All visible bars).")]
        [CoreParameterRange(0, 10000)]
        public int Period 
        { 
            get => _period; 
            set => SetProperty(ref _period, value); 
        }

        private int _rowCount = 50;
        [DisplayName("Row Count")]
        [Description("Number of vertical price histogram buckets.")]
        [CoreParameterRange(5, 200)]
        public int RowCount 
        { 
            get => _rowCount; 
            set => SetProperty(ref _rowCount, value); 
        }

        private PriceType _priceType = PriceType.Close;
        [Browsable(false)]
        [DisplayName("Price Type")]
        [Description("Price source used to determine the residence bucket for each bar.")]
        public PriceType PriceType
        {
            get => _priceType;
            set
            {
                if (SetProperty(ref _priceType, value))
                {
                    OnPropertyChanged(nameof(PriceSource));
                }
            }
        }

        [Browsable(false)]
        public PriceType PriceSource
        {
            get => _priceType;
            set => PriceType = value;
        }

        private double _opacity = 0.3;
        [DisplayName("Opacity")]
        [Description("Transparency level for Time at Price bars.")]
        [CoreParameterRange(0.1, 1.0)]
        public double Opacity 
        { 
            get => _opacity; 
            set => SetProperty(ref _opacity, value); 
        }

        private DisplaySide _side = DisplaySide.Left;
        [DisplayName("Side")]
        [Description("Alignment side for histogram display (Left or Right).")]
        public DisplaySide Side 
        { 
            get => _side; 
            set => SetProperty(ref _side, value); 
        }

        private IndicatorColor? _valueAreaColor = IndicatorDefaultConstants.VolumeProfileValueAreaColor;
        [DisplayName("Value Area Color")]
        [Description("Color for the Value Area (70% residence time). Defaults to yellow (#FFEB3B) if unset.")]
        public IndicatorColor? ValueAreaColor
        {
            get => _valueAreaColor;
            set => SetProperty(ref _valueAreaColor, value);
        }

        public IndicatorColor EffectiveValueAreaColor => _valueAreaColor ?? IndicatorDefaultConstants.VolumeProfileValueAreaColor;

        public override string GetDisplayName(string type) => $"{type} ({Period}, {RowCount}, {PriceType})";

        public override void Validate()
        {
            if (Period < 0 || Period > 10000)
            {
                throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 0 (All) and 10000.");
            }
            if (RowCount < 5 || RowCount > 200)
            {
                throw new ArgumentOutOfRangeException(nameof(RowCount), "Row Count must be between 5 and 200.");
            }
        }
    }
}
