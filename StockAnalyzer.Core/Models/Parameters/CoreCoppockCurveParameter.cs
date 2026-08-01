using System;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreCoppockCurveParameter : CoreIndicatorParameterBase
    {
        [CoreParameterRange(1, 200)]
        public int LongRocPeriod { get; set; } = 14;

        [CoreParameterRange(1, 200)]
        public int ShortRocPeriod { get; set; } = 11;

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
