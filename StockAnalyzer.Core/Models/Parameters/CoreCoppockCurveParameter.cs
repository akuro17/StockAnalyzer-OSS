using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreCoppockCurveParameter : CoreIndicatorParameterBase
    {
        [DisplayName("Long ROC Period")]
        [Description("Long Rate-of-Change period for Coppock Curve.")]
        [Category("ROC Periods")]
        [CoreParameterRange(1, 200)]
        public int LongRocPeriod { get; set; } = 14;

        [DisplayName("Short ROC Period")]
        [Description("Short Rate-of-Change period for Coppock Curve.")]
        [Category("ROC Periods")]
        [CoreParameterRange(1, 200)]
        public int ShortRocPeriod { get; set; } = 11;

        [DisplayName("WMA Period")]
        [Description("Weighted Moving Average smoothing period.")]
        [Category("Smoothing")]
        [CoreParameterRange(1, 200)]
        public int WmaPeriod { get; set; } = 10;

        public override string GetDisplayName(string type) => $"{type} ({LongRocPeriod},{ShortRocPeriod},{WmaPeriod})";

        public override void Validate()
        {
             if (LongRocPeriod < 1 || ShortRocPeriod < 1 || WmaPeriod < 1)
                throw new ArgumentOutOfRangeException("Periods must be greater than 0");
        }
    }
}
