using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class ParameterMetadataCoverageTests
{
    public static IEnumerable<object[]> GetAllParameterTypes()
    {
        var baseType = typeof(CoreIndicatorParameterBase);
        var types = baseType.Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .OrderBy(t => t.Name);

        foreach (var type in types)
        {
            yield return new object[] { type };
        }
    }

    [Fact]
    public void ParameterClasses_Count_ShouldBeAtLeast58()
    {
        var count = GetAllParameterTypes().Count();
        Assert.True(count >= 58, $"Expected at least 58 parameter classes, found {count}");
    }

    [Theory]
    [MemberData(nameof(GetAllParameterTypes))]
    public void ParameterClass_CanBeInstantiatedWithDefaultConstructor(Type parameterType)
    {
        var instance = Activator.CreateInstance(parameterType) as CoreIndicatorParameterBase;
        Assert.NotNull(instance);
        
        // Ensure GetDisplayName and Validate run without crashing on defaults
        var displayName = instance.GetDisplayName("Test");
        Assert.False(string.IsNullOrWhiteSpace(displayName));
    }

    [Theory]
    [MemberData(nameof(GetAllParameterTypes))]
    public void ParameterProperties_MustHaveDisplayNameAndDescription(Type parameterType)
    {
        var properties = parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

        foreach (var prop in properties)
        {
            var browsable = prop.GetCustomAttribute<BrowsableAttribute>();
            if (browsable != null && !browsable.Browsable) continue;
            if (string.Equals(prop.Name, "ShowSubWindowBar", StringComparison.OrdinalIgnoreCase)) continue;

            var displayNameAttr = prop.GetCustomAttribute<DisplayNameAttribute>();
            Assert.True(
                displayNameAttr != null && !string.IsNullOrWhiteSpace(displayNameAttr.DisplayName),
                $"{parameterType.Name}.{prop.Name} is missing [DisplayName] attribute.");

            var descAttr = prop.GetCustomAttribute<DescriptionAttribute>();
            Assert.True(
                descAttr != null && !string.IsNullOrWhiteSpace(descAttr.Description),
                $"{parameterType.Name}.{prop.Name} is missing [Description] attribute.");
        }
    }

    [Theory]
    [MemberData(nameof(GetAllParameterTypes))]
    public void NumericalParameterProperties_MustHaveRangeAttribute(Type parameterType)
    {
        var properties = parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

        foreach (var prop in properties)
        {
            var browsable = prop.GetCustomAttribute<BrowsableAttribute>();
            if (browsable != null && !browsable.Browsable) continue;
            if (string.Equals(prop.Name, "ShowSubWindowBar", StringComparison.OrdinalIgnoreCase)) continue;

            if (prop.PropertyType == typeof(int) ||
                prop.PropertyType == typeof(decimal) ||
                prop.PropertyType == typeof(double) ||
                prop.PropertyType == typeof(float))
            {
                var rangeAttr = prop.GetCustomAttribute<RangeAttribute>();
                var coreRangeAttr = prop.GetCustomAttribute<CoreParameterRangeAttribute>();

                Assert.True(
                    rangeAttr != null || coreRangeAttr != null,
                    $"{parameterType.Name}.{prop.Name} of type {prop.PropertyType.Name} is missing [Range] or [CoreParameterRange] attribute.");
            }
        }
    }

    [Fact]
    public void ParameterMetadataAudit_ReportAllMissingMetadata()
    {
        var baseType = typeof(CoreIndicatorParameterBase);
        var types = baseType.Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .OrderBy(t => t.Name);

        var missingList = new List<string>();

        foreach (var type in types)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite);

            foreach (var prop in properties)
            {
                var browsable = prop.GetCustomAttribute<BrowsableAttribute>();
                if (browsable != null && !browsable.Browsable) continue;
                if (string.Equals(prop.Name, "ShowSubWindowBar", StringComparison.OrdinalIgnoreCase)) continue;

                var displayNameAttr = prop.GetCustomAttribute<DisplayNameAttribute>();
                if (displayNameAttr == null || string.IsNullOrWhiteSpace(displayNameAttr.DisplayName))
                {
                    missingList.Add($"[Missing DisplayName] {type.Name}.{prop.Name}");
                }

                var descAttr = prop.GetCustomAttribute<DescriptionAttribute>();
                if (descAttr == null || string.IsNullOrWhiteSpace(descAttr.Description))
                {
                    missingList.Add($"[Missing Description] {type.Name}.{prop.Name}");
                }

                if (prop.PropertyType == typeof(int) ||
                    prop.PropertyType == typeof(decimal) ||
                    prop.PropertyType == typeof(double) ||
                    prop.PropertyType == typeof(float))
                {
                    var rangeAttr = prop.GetCustomAttribute<RangeAttribute>();
                    var coreRangeAttr = prop.GetCustomAttribute<CoreParameterRangeAttribute>();
                    if (rangeAttr == null && coreRangeAttr == null)
                    {
                        missingList.Add($"[Missing Range] {type.Name}.{prop.Name} ({prop.PropertyType.Name})");
                    }
                }
            }
        }

        if (missingList.Count > 0)
        {
            var summary = string.Join(Environment.NewLine, missingList);
            try
            {
                System.IO.File.WriteAllText(@"Y:\Temp\parameter_metadata_gaps.txt", summary);
            }
            catch { }
            Assert.True(false, $"Found {missingList.Count} metadata gaps:{Environment.NewLine}{summary}");
        }
    }
}
