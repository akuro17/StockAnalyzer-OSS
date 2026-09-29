namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreMarketStructureShiftParameter : CoreIndicatorParameterBase
    {
        [System.ComponentModel.DisplayName("ZigZag Threshold (%)")]
        [System.ComponentModel.Description("Minimum price change percentage required to establish a swing pivot.")]
        [CoreParameterRange(0.1, 50.0)]
        public decimal ZigZagThreshold
        {
            get => _zigZagThreshold;
            set => SetProperty(ref _zigZagThreshold, value);
        }
        private decimal _zigZagThreshold = 5.0m;

        public override string GetDisplayName(string indicatorName)
        {
            return $"{indicatorName} ({ZigZagThreshold}%)";
        }

        public override void Validate()
        {
            if (ZigZagThreshold <= 0)
                throw new System.ArgumentException("ZigZag Threshold must be greater than 0");
        }
    }
}
