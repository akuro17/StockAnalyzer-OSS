using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Serialization;

/// <summary>
/// Automatically discovers all subclasses of CoreIndicatorParameterBase in the Core assembly
/// and registers them for JSON polymorphism, avoiding the need for a huge list of [JsonDerivedType] attributes.
/// </summary>
public static class WorkspacePolymorphicResolver
{
    private static readonly Type[] _parameterTypes;

    static WorkspacePolymorphicResolver()
    {
        // Cache the derived types once per application lifecycle
        _parameterTypes = typeof(CoreIndicatorParameterBase).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(CoreIndicatorParameterBase)))
            .ToArray();
    }

    /// <summary>
    /// Creates a resolver with our custom polymorphism modifier.
    /// </summary>
    public static DefaultJsonTypeInfoResolver CreateResolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(ModifyTypeInfo);
        return resolver;
    }

    /// <summary>
    /// Modifies the JsonTypeInfo for CoreIndicatorParameterBase to register all its subclasses.
    /// </summary>
    private static void ModifyTypeInfo(JsonTypeInfo jsonTypeInfo)
    {
        if (jsonTypeInfo.Type == typeof(CoreIndicatorParameterBase))
        {
            jsonTypeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor,
            };

            foreach (var type in _parameterTypes)
            {
                // Register using the exact type name as the discriminator value
                jsonTypeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name));
            }
        }
    }
}
