using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Common;

public class PolymorphicTypeResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);
        if (type == typeof(CoreIndicatorParameterBase))
        {
            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
                IgnoreUnrecognizedTypeDiscriminators = true,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization
            };
            
            var assembly = typeof(CoreIndicatorParameterBase).Assembly;
            var derivedTypes = assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(CoreIndicatorParameterBase)) && !t.IsAbstract);
            
            foreach (var derived in derivedTypes)
            {
                typeInfo.PolymorphismOptions.DerivedTypes.Add(
                    new JsonDerivedType(derived, derived.Name));
            }
        }
        return typeInfo;
    }
}
