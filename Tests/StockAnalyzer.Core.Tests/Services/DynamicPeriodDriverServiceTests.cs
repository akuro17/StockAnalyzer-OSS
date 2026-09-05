using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class DynamicPeriodDriverServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testFilePath;

    public DynamicPeriodDriverServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "StockAnalyzer_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
        _testFilePath = Path.Combine(_testDirectory, "dynamic_period_drivers.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task SaveAndGetDynamicPeriodDriver_PersistsAndRetrievesCorrectly()
    {
        // Arrange
        using var service = new DynamicPeriodDriverService(_testFilePath);
        var driver = new CoreIndicatorSettings
        {
            Id = "driver_1",
            DisplayName = "HT Dominant Cycle",
            TypeEnum = IndicatorType.HilbertTransform,
            IsOverlay = false
        };

        // Act
        await service.SaveDynamicPeriodDriverAsync(driver);
        var all = await service.GetDynamicPeriodDriversAsync();
        var retrieved = service.GetDynamicPeriodDriver("driver_1");

        // Assert
        Assert.Single(all);
        Assert.Equal("driver_1", all[0].Id);
        Assert.Equal("HT Dominant Cycle", all[0].DisplayName);
        Assert.NotNull(retrieved);
        Assert.Equal("driver_1", retrieved!.Id);
        Assert.True(File.Exists(_testFilePath));
    }

    [Fact]
    public async Task DeleteDynamicPeriodDriver_RemovesFromCacheAndDisk()
    {
        // Arrange
        using var service = new DynamicPeriodDriverService(_testFilePath);
        var driver1 = new CoreIndicatorSettings { Id = "driver_1", DisplayName = "Driver 1" };
        var driver2 = new CoreIndicatorSettings { Id = "driver_2", DisplayName = "Driver 2" };

        await service.SaveDynamicPeriodDriverAsync(driver1);
        await service.SaveDynamicPeriodDriverAsync(driver2);

        // Act
        bool deleted = await service.DeleteDynamicPeriodDriverAsync("driver_1");
        var all = service.GetDynamicPeriodDrivers();

        // Assert
        Assert.True(deleted);
        Assert.Single(all);
        Assert.Equal("driver_2", all[0].Id);
        Assert.Null(service.GetDynamicPeriodDriver("driver_1"));
    }

    [Fact]
    public async Task PersistenceReload_ReadsPersistedDataAcrossInstances()
    {
        // Arrange: Save using first instance
        using (var service1 = new DynamicPeriodDriverService(_testFilePath))
        {
            await service1.SaveDynamicPeriodDriverAsync(new CoreIndicatorSettings
            {
                Id = "persistent_driver",
                DisplayName = "Persistent Driver",
                TypeEnum = IndicatorType.RSI
            });
        }

        // Act: Load using a new second instance
        using (var service2 = new DynamicPeriodDriverService(_testFilePath))
        {
            var loaded = await service2.GetDynamicPeriodDriversAsync();

            // Assert
            Assert.Single(loaded);
            Assert.Equal("persistent_driver", loaded[0].Id);
            Assert.Equal("Persistent Driver", loaded[0].DisplayName);
        }
    }
}
