using System;
using System.Threading.Tasks;
using StockAnalyzer.Services;
using Xunit;

namespace StockAnalyzer.Tests.Services
{
    public class IndicatorInfrastructureTests
    {
        [Fact]
        public void Factory_CreateIndicator_WithInvalidType_ReturnsNull()
        {
            // Priority 4: Verify Factory Fallback safety
            var invalidSettings = new IndicatorSettings
            {
                Type = "NonExistentIndicatorType",
                TypeEnum = null
            };

            // Should essentially fail gracefully (return null) and not throw exception
            var result = IndicatorFactory.Default.Create(invalidSettings.TypeEnum.Value, invalidSettings.ParameterObject);

            Assert.Null(result);
        }

        [Fact]
        public void Registry_Register_DuplicateKey_ThrowsInvalidOperation()
        {
            // Priority 5: Verify Registry Concurrency/Integrity
            // Since Registry is static and pre-initialized in app, we test the behavior.
            // CAUTION: RegisterAllFactories() might have already run if other tests touched it.
            // However, we can try to register a NEW unique type to test the logic,
            // or try to re-register an existing one (SMA) to verify it throws.
            
            // Let's try to verify that re-registering SMA throws.
            // Assuming SMA is already registered or will be registered.
            
            // First ensure it's initialized (by calling GetRegisteredTypes)
            IndicatorRegistry.GetRegisteredTypes();

            Assert.Throws<InvalidOperationException>(() =>
            {
                // Trying to register SMA again with a dummy factory
                IndicatorRegistry.Register(IndicatorType.SMA, _ => new StockAnalyzer.Core.Models.SmaIndicator());
            });
        }
    }
}
