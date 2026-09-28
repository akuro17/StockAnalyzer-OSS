using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace StockAnalyzer.Generators
{
    [Generator]
    public class IndicatorSourceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // 1. Find the IndicatorType enum and extract its members
            var enumProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => s is EnumDeclarationSyntax eds && eds.Identifier.Text == "IndicatorType",
                    transform: (ctx, _) => GetEnumMembers(ctx))
                .Where(m => m != null);

            // 2. Find classes with [StockIndicator] attribute
            var indicatorProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => s is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
                    transform: (ctx, _) => GetIndicatorClasses(ctx))
                .Where(m => m != null);

            // 3. Combine and Generate
            var compilation = context.CompilationProvider.Combine(enumProvider.Collect()).Combine(indicatorProvider.Collect());

            context.RegisterSourceOutput(compilation, (spc, source) =>
            {
                var (compilationAndEnums, indicators) = source;
                var (compilationVal, enums) = compilationAndEnums;
                
                // Consistency Check: Ensure all 125 types in Enum are implemented or acknowledged
                // (Optional: Emit diagnostics if missing)

                // Generate Extension Methods
                GenerateRegistryExtensions(spc, indicators);
                
                // Generate JsonDerivedType attributes for IndicatorParameterBase
                GenerateJsonPolymorphicConfiguration(spc, indicators);
            });
        }

        private static List<EnumMemberModel> GetEnumMembers(GeneratorSyntaxContext context)
        {
            var enumDeclaration = (EnumDeclarationSyntax)context.Node;
            var members = new List<EnumMemberModel>();

            foreach (var member in enumDeclaration.Members)
            {
                var name = member.Identifier.Text;
                var description = "No Description"; // Ideally extract from attribute
                
                // Basic extraction of Description attribute if needed
                // ...

                members.Add(new EnumMemberModel { Name = name, Description = description });
            }

            return members;
        }

        private static IndicatorModel GetIndicatorClasses(GeneratorSyntaxContext context)
        {
            var classDeclaration = (ClassDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
            
            if (symbol == null) return null;

            var attribute = symbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "StockIndicatorAttribute" || a.AttributeClass?.Name == "StockIndicator");

            if (attribute == null) return null;

            // Extract IndicatorTypeName (string, second argument)
            string indicatorTypeName = "";
            if (attribute.ConstructorArguments.Length > 1)
            {
                indicatorTypeName = attribute.ConstructorArguments[1].Value?.ToString() ?? "";
            }

            // Extract generic type argument from defining class: e.g., IndicatorBase<SmaParameter>
            // This is tricky. We need the Parameter Type.
            // Assumption: The class inherits from IndicatorBase<TParam>
            
            var baseType = symbol.BaseType;
            string parameterClassName = "NoParameter";
            string parameterFullTypeName = "StockAnalyzer.Models.Parameters.NoParameter";

            while (baseType != null)
            {
                if (baseType.Name == "IndicatorBase" && baseType.TypeArguments.Length > 0)
                {
                    var paramType = baseType.TypeArguments[0];
                    parameterClassName = paramType.Name;
                    parameterFullTypeName = paramType.ToDisplayString();
                    break;
                }
                baseType = baseType.BaseType;
            }

            return new IndicatorModel
            {
                TypeName = indicatorTypeName,
                ClassName = symbol.Name,
                Namespace = symbol.ContainingNamespace.ToDisplayString(),
                ParameterClassName = parameterClassName,
                ParameterFullTypeName = parameterFullTypeName
            };
        }

        private static void GenerateRegistryExtensions(SourceProductionContext context, ImmutableArray<IndicatorModel> indicators)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using StockAnalyzer.Models;");
            sb.AppendLine("using StockAnalyzer.Services;");
            sb.AppendLine();
            sb.AppendLine("namespace StockAnalyzer.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public static class IndicatorRegistryExtensions");
            sb.AppendLine("    {");
            sb.AppendLine("        public static void RegisterAutoGeneratedIndicators(this IIndicatorRegistry registry)");
            sb.AppendLine("        {");

            foreach (var ind in indicators.Distinct(new IndicatorComparer()))
            {
                if (string.IsNullOrEmpty(ind.TypeName)) continue;
                
                sb.AppendLine($"            // Register {ind.TypeName}");
                sb.AppendLine($"            registry.Register(IndicatorType.{ind.TypeName}, typeof({ind.Namespace}.{ind.ClassName}));");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource("IndicatorRegistryExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        private static void GenerateJsonPolymorphicConfiguration(SourceProductionContext context, ImmutableArray<IndicatorModel> indicators)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Text.Json.Serialization;");
            sb.AppendLine("using StockAnalyzer.Models;");
            sb.AppendLine("using StockAnalyzer.Models.Parameters;");
            sb.AppendLine();
            sb.AppendLine("namespace StockAnalyzer.Models");
            sb.AppendLine("{");
            sb.AppendLine("    // Auto-generated polymorphic configuration");
            
            var uniqueParams = indicators
                .Select(i => new { i.ParameterClassName, i.ParameterFullTypeName })
                .Distinct()
                .OrderBy(p => p.ParameterClassName);

            // Extending the partial class IndicatorParameterBase
            // [JsonPolymorphic] is already on the base class, do not duplicate
            
            foreach (var p in uniqueParams)
            {
                if (p.ParameterClassName == "NoParameter") continue; // Usually handled manually or default

                sb.AppendLine($"    [JsonDerivedType(typeof({p.ParameterFullTypeName}), typeDiscriminator: \"{p.ParameterClassName}\")]");
            }
            
            sb.AppendLine("    public partial class IndicatorParameterBase");
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource("IndicatorParameterBase.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }
}
