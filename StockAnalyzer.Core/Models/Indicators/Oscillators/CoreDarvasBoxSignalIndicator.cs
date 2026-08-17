using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.DarvasBoxSignal)]
    public class CoreDarvasBoxSignalIndicator : CoreIndicatorBase
    {
        public override string Name => "Darvas Box Signal";

        public int HighPeriod { get; set; } = 20;
        public int ConfirmationPeriod { get; set; } = 3;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreDarvasBoxSignalParameter p)
            {
                HighPeriod = p.HighPeriod;
                ConfirmationPeriod = p.ConfirmationPeriod;
            }
        }

        private enum State { Searching, Confirming, Boxed }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (candles.Count < HighPeriod)
            {
                for (int i = 0; i < candles.Count; i++)
                {
                    _values.Add(null);
                }
                return IndicatorResult.Success(_values);
            }

            for (int i = 0; i < HighPeriod - 1; i++)
            {
                _values.Add(null);
            }

            var state = State.Searching;
            decimal boxTop = 0;
            decimal boxBottom = 0;
            int confirmationDays = 0;

            for (int i = HighPeriod - 1; i < candles.Count; i++)
            {
                var currentCandle = candles[i];
                var highForPeriod = candles.Skip(i - HighPeriod + 1).Take(HighPeriod).Max(c => c.High);
                decimal? signal = null;

                switch (state)
                {
                    case State.Searching:
                        if (currentCandle.High >= highForPeriod)
                        {
                            boxTop = currentCandle.High;
                            boxBottom = currentCandle.Low;
                            confirmationDays = 0;
                            state = State.Confirming;
                        }
                        break;

                    case State.Confirming:
                        if (currentCandle.High >= boxTop)
                        {
                            boxTop = currentCandle.High;
                            boxBottom = currentCandle.Low;
                            confirmationDays = 0;
                        }
                        else
                        {
                            confirmationDays++;
                            boxBottom = Math.Min(boxBottom, currentCandle.Low);
                            if (confirmationDays >= ConfirmationPeriod)
                            {
                                state = State.Boxed;
                            }
                        }
                        break;

                    case State.Boxed:
                        if (currentCandle.Close > boxTop)
                        {
                            signal = 1;
                            state = State.Searching;
                        }
                        else if (currentCandle.Close < boxBottom)
                        {
                            signal = -1;
                            state = State.Searching;
                        }
                        else
                        {
                            signal = 0;
                        }
                        break;
                }
                _values.Add(signal);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
