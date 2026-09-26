using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Permanent, registry-wide replacement for the one-off reflection survey used while diagnosing the
/// SMA/EMA <c>Period</c> default mismatch (see <c>CoreIndicatorBaseGetDefaultSettingsTests</c> and
/// <c>Y:\Temp\sa_step_log_TrainingWizard_IndicatorChannelFeatureExporter.md</c>'s "SMA/EMA
/// registry-default Period mismatch" section). That survey only compared each indicator's own
/// <c>Period</c> field against its naming-convention parameter class's <c>Period</c> field. This test
/// generalizes the same check to every matching-named scalar property (not just <c>Period</c>) across
/// every registered <see cref="IndicatorType"/>, so a future indicator whose own default drifts from
/// its parameter class's default under a different property name (e.g. <c>Length</c>, <c>FastPeriod</c>)
/// is caught here instead of silently reproducing the SMA/EMA class of bug.
///
/// <para>
/// The scalar-type predicate is <see cref="FeatureChannelConverter.IsScalar"/> /
/// <see cref="FeatureChannelConverter.ScalarProperties"/> themselves (widened to <c>internal</c> so this
/// test project can call them directly, rather than maintaining a separate duplicated copy): that
/// definition governs which properties <see cref="FeatureChannel.Params"/> can actually carry, so a
/// mismatch this test would not see is also a mismatch <c>FeatureChannelConverter.ApplyParams</c> could
/// never apply anyway.
/// </para>
/// </summary>
public class IndicatorParameterDefaultParityTests
{
    [Fact]
    public void AllRegisteredIndicators_MatchingNamedScalarProperties_AgreeWithGetDefaultSettingsBaseline()
    {
        var mismatches = new List<string>();

        foreach (var type in IndicatorFactory.Default.GetRegisteredTypes())
        {
            var indicator = IndicatorFactory.Default.Create(type);
            if (indicator is null)
            {
                mismatches.Add($"{type}: IIndicatorFactory.Create returned null for a registered type.");
                continue;
            }

            var settings = indicator.GetDefaultSettings();
            var parameterObject = settings.ParameterObject;
            if (parameterObject is null)
            {
                // No naming-convention parameter class and no last-resort fallback applied to this
                // indicator (e.g. a periodless indicator like OBV/BOP/ADL) -- nothing to compare.
                continue;
            }

            var indicatorProps = FeatureChannelConverter.ScalarProperties(indicator.GetType()).ToDictionary(p => p.Name, p => p);
            foreach (var paramProp in FeatureChannelConverter.ScalarProperties(parameterObject.GetType()))
            {
                if (!indicatorProps.TryGetValue(paramProp.Name, out var indicatorProp))
                {
                    continue;
                }

                if (indicatorProp.PropertyType != paramProp.PropertyType)
                {
                    continue;
                }

                var indicatorValue = indicatorProp.GetValue(indicator);
                var paramValue = paramProp.GetValue(parameterObject);
                if (!Equals(indicatorValue, paramValue))
                {
                    mismatches.Add(
                        $"{type}: property '{paramProp.Name}' -- indicator class default = " +
                        $"'{indicatorValue}', {parameterObject.GetType().Name} default = '{paramValue}'.");
                }
            }
        }

        Assert.True(mismatches.Count == 0, "Indicator/Parameter default mismatches found:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public void AllRegisteredParameterClasses_DeclareNoNullableScalarProperty()
    {
        var violations = new List<string>();
        var parameterTypes = typeof(CoreIndicatorParameterBase).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(CoreIndicatorParameterBase).IsAssignableFrom(t));

        foreach (var parameterType in parameterTypes)
        {
            foreach (var prop in FeatureChannelConverter.ScalarProperties(parameterType))
            {
                if (FeatureChannelConverter.IsNullableScalar(prop.PropertyType))
                {
                    violations.Add($"{parameterType.Name}.{prop.Name} ({prop.PropertyType.Name})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "FeatureChannelConverter.ExtractParams cannot represent an explicit 'reset to null' edit " +
            "for a Nullable scalar property (documented, previously 'unreachable' gap); the following " +
            "registered parameter classes have newly introduced one:\n" + string.Join("\n", violations));
    }
}
