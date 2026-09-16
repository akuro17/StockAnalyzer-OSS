using System;
using System.Collections.Generic;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class TemplateModelTests
{
    [Fact]
    public void IndicatorTemplate_Defaults_ShouldBeValid()
    {
        // Act
        var template = new IndicatorTemplate
        {
            Name = "My Indicator Template",
            Description = "Test description"
        };

        // Assert
        Assert.NotEqual(Guid.Empty, template.Id);
        Assert.Equal("My Indicator Template", template.Name);
        Assert.Equal(TemplateType.Indicator, template.TemplateType);
        Assert.Equal(TemplateBase.DefaultSchemaVersion, template.SchemaVersion);
        Assert.NotNull(template.Indicators);
        Assert.Empty(template.Indicators);
        Assert.False(template.IsSystem);
        Assert.False(template.IsFavorite);
    }

    [Fact]
    public void ColumnTemplate_Defaults_ShouldBeValid()
    {
        // Act
        var template = new ColumnTemplate
        {
            Name = "My Column Template"
        };

        // Assert
        Assert.NotEqual(Guid.Empty, template.Id);
        Assert.Equal("My Column Template", template.Name);
        Assert.Equal(TemplateType.Column, template.TemplateType);
        Assert.Equal(TemplateBase.DefaultSchemaVersion, template.SchemaVersion);
        Assert.NotNull(template.ColumnNames);
        Assert.Empty(template.ColumnNames);
    }

    [Fact]
    public void IndicatorTemplate_SetIndicators_ShouldUpdateAndPreventNull()
    {
        var template = new IndicatorTemplate();
        template.SetIndicators(new[] { new CoreIndicatorSettings { DisplayName = "EMA" } });
        Assert.Single(template.Indicators);
        Assert.Equal("EMA", template.Indicators[0].DisplayName);

        template.SetIndicators(null!);
        Assert.NotNull(template.Indicators);
        Assert.Empty(template.Indicators);
    }

    [Fact]
    public void ColumnTemplate_SetColumnNames_ShouldUpdateAndPreventNull()
    {
        var template = new ColumnTemplate();
        template.SetColumnNames(new[] { "Symbol", "Close" });
        Assert.Equal(2, template.ColumnNames.Count);
        Assert.Equal("Symbol", template.ColumnNames[0]);

        template.SetColumnNames(null!);
        Assert.NotNull(template.ColumnNames);
        Assert.Empty(template.ColumnNames);
    }

    [Fact]
    public void IndicatorTemplate_Serialization_ShouldPreservePropertiesAndPolymorphism()
    {
        // Arrange
        var template = new IndicatorTemplate
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Name = "Trend Strategy",
            Description = "SMA + Volume template",
            SchemaVersion = 1,
            IsFavorite = true,
            Indicators = new List<CoreIndicatorSettings>
            {
                new()
                {
                    Id = "sma-1",
                    DisplayName = "SMA(25)",
                    TypeEnum = IndicatorType.SMA,
                    ParameterObject = new CoreSmaParameter { Period = 25 }
                }
            }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver()
        };

        // Act
        var json = JsonSerializer.Serialize(template, options);
        var deserialized = JsonSerializer.Deserialize<IndicatorTemplate>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(template.Id, deserialized.Id);
        Assert.Equal(template.Name, deserialized.Name);
        Assert.Equal(TemplateType.Indicator, deserialized.TemplateType);
        Assert.Equal(template.Description, deserialized.Description);
        Assert.True(deserialized.IsFavorite);
        Assert.Single(deserialized.Indicators);

        var ind = deserialized.Indicators[0];
        Assert.Equal("sma-1", ind.Id);
        Assert.Equal(IndicatorType.SMA, ind.TypeEnum);
        Assert.IsType<CoreSmaParameter>(ind.ParameterObject);
        Assert.Equal(25, ((CoreSmaParameter)ind.ParameterObject).Period);
    }

    [Fact]
    public void ColumnTemplate_Serialization_ShouldPreserveColumns()
    {
        // Arrange
        var template = new ColumnTemplate
        {
            Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Name = "Valuation Grid",
            ColumnNames = new List<string> { "Symbol", "Close", "PER", "PBR", "DividendYield" }
        };

        // Act
        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<ColumnTemplate>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(template.Id, deserialized.Id);
        Assert.Equal("Valuation Grid", deserialized.Name);
        Assert.Equal(TemplateType.Column, deserialized.TemplateType);
        Assert.Equal(5, deserialized.ColumnNames.Count);
        Assert.Equal("Symbol", deserialized.ColumnNames[0]);
        Assert.Equal("DividendYield", deserialized.ColumnNames[4]);
    }

    [Fact]
    public void TemplateValidationResult_ShouldReflectValidity()
    {
        // Act & Assert
        var success = TemplateValidationResult.Success();
        Assert.True(success.IsValid);
        Assert.Equal(TemplateValidationSeverity.Valid, success.Severity);
        Assert.Empty(success.Errors);

        var warning = TemplateValidationResult.WithWarning("Minor deprecation warning");
        Assert.True(warning.IsValid);
        Assert.Equal(TemplateValidationSeverity.Warning, warning.Severity);
        Assert.Single(warning.Warnings);

        var failure = TemplateValidationResult.Failure("Missing required indicator");
        Assert.False(failure.IsValid);
        Assert.Equal(TemplateValidationSeverity.Invalid, failure.Severity);
        Assert.Single(failure.Errors);
    }

    [Fact]
    public void ThemeTemplate_Defaults_ShouldBeValid()
    {
        // Act
        var template = new ThemeTemplate
        {
            Name = "Cyberpunk Dark"
        };

        // Assert
        Assert.NotEqual(Guid.Empty, template.Id);
        Assert.Equal("Cyberpunk Dark", template.Name);
        Assert.Equal(TemplateType.Theme, template.TemplateType);
        Assert.Equal(TemplateBase.DefaultSchemaVersion, template.SchemaVersion);
        Assert.NotNull(template.Colors);
        Assert.True(template.Colors.IsDark);
    }

    [Fact]
    public void ThemeTemplate_Serialization_ShouldPreserveColors()
    {
        // Arrange
        var customColors = ThemeColors.Dark with
        {
            ChartBackground = IndicatorColor.FromUInt(0xFF0D1117),
            ShellAccent = IndicatorColor.FromUInt(0xFF58A6FF)
        };

        var template = new ThemeTemplate
        {
            Id = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            Name = "GitHub Dark",
            Description = "Dark mode matching GitHub colors",
            Colors = customColors
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new IndicatorColorJsonConverter() }
        };

        // Act
        var json = JsonSerializer.Serialize(template, options);
        var deserialized = JsonSerializer.Deserialize<ThemeTemplate>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(template.Id, deserialized.Id);
        Assert.Equal("GitHub Dark", deserialized.Name);
        Assert.Equal(TemplateType.Theme, deserialized.TemplateType);
        Assert.Equal("Dark mode matching GitHub colors", deserialized.Description);
        Assert.NotNull(deserialized.Colors);
        Assert.Equal(customColors.ChartBackground, deserialized.Colors.ChartBackground);
        Assert.Equal(customColors.ShellAccent, deserialized.Colors.ShellAccent);
    }
}
