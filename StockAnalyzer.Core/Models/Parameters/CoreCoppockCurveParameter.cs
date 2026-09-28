using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreCoppockCurveParameter : CoreIndicatorParameterBase
    {
        private int _longRocPeriod = 14;

        [DisplayName("Long ROC Period")]
        [Description("Long Rate-of-Change period for Coppock Curve.")]
        [Category("ROC Periods")]
        [CoreParameterRange(1, 200)]
        public int LongRocPeriod
        {
            get => _longRocPeriod;
            set => SetProperty(ref _longRocPeriod, value);
        }

        private int _shortRocPeriod = 11;

        [DisplayName("Short ROC Period")]
        [Description("Short Rate-of-Change period for Coppock Curve.")]
        [Category("ROC Periods")]
        [CoreParameterRange(1, 200)]
        public int ShortRocPeriod
        {
            get => _shortRocPeriod;
            set => SetProperty(ref _shortRocPeriod, value);
        }

        private int _wmaPeriod = 10;

        [DisplayName("WMA Period")]
        [Description("Weighted Moving Average smoothing period.")]
        [Category("Smoothing")]
        [CoreParameterRange(1, 200)]
        public int WmaPeriod
        {
            get => _wmaPeriod;
            set => SetProperty(ref _wmaPeriod, value);
        }

        public override string GetDisplayName(string type) => $"{type} ({LongRocPeriod},{ShortRocPeriod},{WmaPeriod})";

        public override int GetRequiredWarmupBars() => Math.Max(LongRocPeriod, ShortRocPeriod) + WmaPeriod;

        public override void Validate()
        {
             if (LongRocPeriod < 1 || ShortRocPeriod < 1 || WmaPeriod < 1)
                throw new ArgumentOutOfRangeException("Periods must be greater than 0");
        }
    }
}
