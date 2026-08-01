using System;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreVolumeProfileParameter : CoreIndicatorParameterBase
    {
        private int _period = 0;
        [CoreParameterRange(0, 10000)]
        public int Period 
        { 
            get => _period; 
            set => SetProperty(ref _period, value); 
        }

        private int _rowCount = 50;
        [CoreParameterRange(5, 200)]
        public int RowCount 
        { 
            get => _rowCount; 
            set => SetProperty(ref _rowCount, value); 
        }
        
        private double _opacity = 0.3;
        [CoreParameterRange(0.1, 1.0)]
        public double Opacity 
        { 
            get => _opacity; 
            set => SetProperty(ref _opacity, value); 
        }

        private VolumeDistributionMode _mode = VolumeDistributionMode.Proportional;
        [System.ComponentModel.DisplayName("Mode")]
        public VolumeDistributionMode Mode 
        { 
            get => _mode; 
            set => SetProperty(ref _mode, value); 
        }
        
        private DisplaySide _side = DisplaySide.Left;
        [System.ComponentModel.DisplayName("Side")]
        public DisplaySide Side 
        { 
            get => _side; 
            set => SetProperty(ref _side, value); 
        }
        
        // For compatibility if needed, map NumberOfBuckets to RowCount
        public int NumberOfBuckets => RowCount;

        public override string GetDisplayName(string type) => $"{type} ({Period}, {RowCount})";

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
