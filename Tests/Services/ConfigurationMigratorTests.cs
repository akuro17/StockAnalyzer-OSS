using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StockAnalyzer.Services;
using Xunit;

namespace StockAnalyzer.Tests.Services;

public class ConfigurationMigratorTests : IDisposable
{
    private readonly string _testFilePath;

    public ConfigurationMigratorTests()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
        
        // Clean up backups
        var dir = Path.GetDirectoryName(_testFilePath);
        if (dir != null)
        {
            var baseName = Path.GetFileName(_testFilePath);
            foreach (var f in Directory.GetFiles(dir, baseName + ".v2.backup.*"))
            {
                File.Delete(f);
            }
        }
    }

    [Fact]
    public void MigrateSettings_ShouldPopulateTypeEnum_WhenMissing()
    {
        // Arrange: Create legacy settings file (Type only, no TypeEnum)
        var legacyJson = @"
[
  {
    ""Type"": ""SMA"",
    ""Category"": ""Trend"",
    ""IsEnabled"": true,
    ""Color"": ""#FFFF0000"",
    ""Thickness"": 1.5,
    ""Style"": ""Solid""
  },
  {
    ""Type"": ""RSI"",
    ""Category"": ""Oscillator"",
    ""IsEnabled"": false,
    ""Color"": ""#FF0000FF"",
    ""Thickness"": 1.0,
    ""Style"": ""Solid""
  }
]";
        File.WriteAllText(_testFilePath, legacyJson);

        // Act
        ConfigurationMigrator.MigrateSettings(_testFilePath);

        // Assert
        var migratedJson = File.ReadAllText(_testFilePath);
        Assert.Contains("\"TypeEnum\"", migratedJson);
        
        // Parse and verify
        var rootNode = System.Text.Json.Nodes.JsonNode.Parse(migratedJson);
        Assert.NotNull(rootNode);
        
        var array = rootNode.AsArray();
        Assert.Equal(2, array.Count);
        
        // Check SMA
        var smaObj = array[0]!.AsObject();
        Assert.Equal("SMA", smaObj["Type"]!.GetValue<string>());
        Assert.Equal((int)IndicatorType.SMA, smaObj["TypeEnum"]!.GetValue<int>());
        
        // Check RSI
        var rsiObj = array[1]!.AsObject();
        Assert.Equal("RSI", rsiObj["Type"]!.GetValue<string>());
        Assert.Equal((int)IndicatorType.RSI, rsiObj["TypeEnum"]!.GetValue<int>());

        // Verify backup creation
        var dir = Path.GetDirectoryName(_testFilePath);
        var backups = Directory.GetFiles(dir!, Path.GetFileName(_testFilePath) + ".v2.backup.*");
        Assert.NotEmpty(backups);
    }

    [Fact]
    public void MigrateSettings_ShouldDoNothing_WhenFileDoesNotExist()
    {
        // Act & Assert (Should not throw)
        ConfigurationMigrator.MigrateSettings("non_existent_file.json");
    }

    [Fact]
    public void MigrateSettings_ShouldDoNothing_WhenAlreadyMigrated()
    {
        // Arrange: Create migrated settings file
        var migratedJson = @"
[
  {
    ""Type"": ""SMA"",
    ""TypeEnum"": 0
  }
]";
        File.WriteAllText(_testFilePath, migratedJson);

        var lastWriteTime = File.GetLastWriteTime(_testFilePath);
        System.Threading.Thread.Sleep(100); // Ensure time difference if file is modified

        // Act
        ConfigurationMigrator.MigrateSettings(_testFilePath);

        // Assert: File should not be modified
        var currentWriteTime = File.GetLastWriteTime(_testFilePath);
        Assert.Equal(lastWriteTime, currentWriteTime);
        
        // No backup should be created
        var dir = Path.GetDirectoryName(_testFilePath);
        var backups = Directory.GetFiles(dir!, Path.GetFileName(_testFilePath) + ".v2.backup.*");
        Assert.Empty(backups);
    }
}
