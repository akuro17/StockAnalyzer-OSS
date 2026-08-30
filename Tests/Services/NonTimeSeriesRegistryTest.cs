using Xunit;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Services;
using StockAnalyzer.Services.Factories.Indicators;

namespace StockAnalyzer.Tests.Services
{
    public class NonTimeSeriesRegistryTest
    {
        [Fact]
        public void CanCreateRenkoChart()
        {
            var settings = new IndicatorSettings
            {
                TypeEnum = IndicatorType.RenkoChart,
                ParameterObject = new RenkoParameter { BrickSize = 0.5m }
            };

            var indicator = IndicatorRegistry.TryCreate(IndicatorType.RenkoChart, settings);
            
            Assert.NotNull(indicator);
            Assert.IsType<RenkoChartIndicator>(indicator);
        }

        [Fact]
        public void CanCreateKagiChart()
        {
            var settings = new IndicatorSettings
            {
                TypeEnum = IndicatorType.KagiChart,
                ParameterObject = new KagiParameter { ReversalRate = 3.0m }
            };

            var indicator = IndicatorRegistry.TryCreate(IndicatorType.KagiChart, settings);
            
            Assert.NotNull(indicator);
            Assert.IsType<KagiChartIndicator>(indicator);
        }

        [Fact]
        public void CanCreatePointAndFigureChart()
        {
            var settings = new IndicatorSettings
            {
                TypeEnum = IndicatorType.PointAndFigureChart,
                ParameterObject = new PointAndFigureParameter { BoxSize = 1.0m, ReversalCount = 3 }
            };

            var indicator = IndicatorRegistry.TryCreate(IndicatorType.PointAndFigureChart, settings);
            
            Assert.NotNull(indicator);
            Assert.IsType<PointAndFigureChartIndicator>(indicator);
        }
    }
}
