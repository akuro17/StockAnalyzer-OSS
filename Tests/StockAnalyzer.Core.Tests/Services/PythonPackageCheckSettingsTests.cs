using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PythonPackageCheckSettingsTests
    {
        private static IStockAnalyzerSettings Create(PythonPackageCheckSettings? packageCheck)
        {
            return new StockAnalyzerSettings(
                new ConfigurationBuilder().Build(),
                Options.Create(new PythonSettings()),
                Options.Create(new ChartDefaultSettings()),
                Options.Create(new PredictionSettings()),
                Options.Create(new ScreenerSettings()),
                Options.Create(new SmartScreenerSettings()),
                Options.Create(new InfrastructureSettings()),
                Options.Create(new MarketStructureSettings()),
                Options.Create(new PatternRecognitionSettings()),
                Options.Create(new ResilienceSettings()),
                Options.Create(new LocalizationSettings()),
                pythonPackageCheckOptions: packageCheck is null ? null : Options.Create(packageCheck));
        }

        [Fact]
        public void Defaults_AreValidAndTheRequestEndsBeforeTheProcess()
        {
            var settings = new PythonPackageCheckSettings();

            settings.Validate();

            Assert.True(settings.UpdateCheckRequestTimeoutSeconds < settings.UpdateCheckTimeoutSeconds);
        }

        [Fact]
        public void DefaultImportProbeTimeout_CoversSlowPackageImports()
        {
            // tensorflow imports in about 5 s; a 5 s probe reported it as missing.
            Assert.True(new PythonPackageCheckSettings().ImportProbeTimeoutSeconds >= 30);
        }

        [Fact]
        public void WithoutTheSection_TheSettingsInterfaceGivesTheDefaults()
        {
            var defaults = new PythonPackageCheckSettings();

            IStockAnalyzerSettings settings = Create(null);

            Assert.Equal(TimeSpan.FromSeconds(defaults.ImportProbeTimeoutSeconds), settings.PythonImportProbeTimeout);
            Assert.Equal(TimeSpan.FromSeconds(defaults.UpdateCheckTimeoutSeconds), settings.PythonUpdateCheckTimeout);
            Assert.Equal(TimeSpan.FromSeconds(defaults.UpdateCheckRequestTimeoutSeconds), settings.PythonUpdateCheckRequestTimeout);
        }

        [Fact]
        public void ConfiguredValues_ReachTheSettingsInterface()
        {
            IStockAnalyzerSettings settings = Create(new PythonPackageCheckSettings
            {
                ImportProbeTimeoutSeconds = 90,
                UpdateCheckTimeoutSeconds = 10,
                UpdateCheckRequestTimeoutSeconds = 4,
            });

            Assert.Equal(TimeSpan.FromSeconds(90), settings.PythonImportProbeTimeout);
            Assert.Equal(TimeSpan.FromSeconds(10), settings.PythonUpdateCheckTimeout);
            Assert.Equal(TimeSpan.FromSeconds(4), settings.PythonUpdateCheckRequestTimeout);
        }

        [Theory]
        [InlineData(PythonPackageCheckLimits.ImportProbeMinSeconds - 1, 5, 3)]
        [InlineData(PythonPackageCheckLimits.ImportProbeMaxSeconds + 1, 5, 3)]
        [InlineData(60, PythonPackageCheckLimits.UpdateCheckMinSeconds - 1, 1)]
        [InlineData(60, PythonPackageCheckLimits.UpdateCheckMaxSeconds + 1, 3)]
        [InlineData(60, 5, PythonPackageCheckLimits.UpdateRequestMinSeconds - 1)]
        [InlineData(60, PythonPackageCheckLimits.UpdateCheckMaxSeconds, PythonPackageCheckLimits.UpdateRequestMaxSeconds + 1)]
        // The request limit must be smaller than the process limit.
        [InlineData(60, 5, 5)]
        [InlineData(60, 5, 9)]
        public void AValueOutsideTheContract_StopsTheSettingsFromBeingBuilt(int importSeconds, int updateSeconds, int requestSeconds)
        {
            var invalid = new PythonPackageCheckSettings
            {
                ImportProbeTimeoutSeconds = importSeconds,
                UpdateCheckTimeoutSeconds = updateSeconds,
                UpdateCheckRequestTimeoutSeconds = requestSeconds,
            };

            Assert.Throws<InvalidOperationException>(() => Create(invalid));
        }
    }
}
