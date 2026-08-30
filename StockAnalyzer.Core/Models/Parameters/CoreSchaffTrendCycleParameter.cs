using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreSchaffTrendCycleParameter : CoreIndicatorParameterBase
    {
        private int _cyclePeriod = 10;

        [DisplayName("Cycle Period")]
        [Description("Stochastic cycle lookback period for STC.")]
        [CoreParameterRange(2, 200)]
        public int CyclePeriod
        {
            get => _cyclePeriod;
            set => SetProperty(ref _cyclePeriod, value);
        }

        private int _shortPeriod = 23;

        [DisplayName("Short Period")]
        [Description("Short EMA period for MACD component of STC.")]
        [CoreParameterRange(2, 200)]
        public int ShortPeriod
        {
            get => _shortPeriod;
            set => SetProperty(ref _shortPeriod, value);
        }

        private int _longPeriod = 50;

        [DisplayName("Long Period")]
        [Description("Long EMA period for MACD component of STC.")]
        [CoreParameterRange(2, 200)]
        public int LongPeriod
        {
            get => _longPeriod;
            set => SetProperty(ref _longPeriod, value);
        }

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
