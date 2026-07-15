using System;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreSchaffTrendCycleParameter : CoreIndicatorParameterBase
    {
        [CoreParameterRange(2, 200)]
        public int CyclePeriod { get; set; } = 10;

        [CoreParameterRange(2, 200)]
        public int ShortPeriod { get; set; } = 23;

        [CoreParameterRange(2, 200)]
        public int LongPeriod { get; set; } = 50;

        public override string GetDisplayName(string type) => $"{type} ({CyclePeriod},{ShortPeriod},{LongPeriod})";

        public override void Validate()
        {
            if (CyclePeriod < 2 || ShortPeriod < 2 || LongPeriod < 2)
                 throw new ArgumentOutOfRangeException("Periods must be 2 or greater");
            if (ShortPeriod >= LongPeriod)
                 throw new ArgumentException("ShortPeriod must be less than LongPeriod");
        }
    }
}
