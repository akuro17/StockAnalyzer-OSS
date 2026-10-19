using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Enforces the rule written in <c>AssemblyInfo.cs</c>: a test class that starts a real child process runs in the <c>ChildProcess</c> collection, alone.
/// A child started beside another test inherits that test's file handles (an ONNX model it is loading), keeps them locked while it lives and makes the
/// other test's cleanup fail intermittently. The rule used to be a convention and was broken four times before this guard existed.
/// The scan reads the sources of this project: every class declared in a file that calls a process starter, and that has test methods, must carry
/// <c>[Collection("ChildProcess")]</c> or another collection that is defined with <c>DisableParallelization = true</c> (on any part of a partial class): what
/// matters is that the class runs alone, and a class that also needs another collection's isolation (the portfolio tests that change the UI-automation
/// environment variable) can have only one collection.
/// </summary>
public sealed class ChildProcessCollectionGuardTests
{
    private const string CollectionName = "ChildProcess";
    private const string ProjectFile = "StockAnalyzer.Core.Tests.csproj";
    private static readonly string ProjectDirectory = Path.Combine("Tests", "StockAnalyzer.Core.Tests");

    // The calls that start a real child process, directly or through the trainers' pump, the Python test runner or the portfolio script helper. The opt-in
    // facts of the portfolio tests (SmokeFact, AblationFact) and heavy child process facts (ChildProcessFact, ChildProcessTheory) are the tests that run the real external workers.
    private static readonly Regex ProcessStarters = new(
        @"Process\.Start\(|new\s+Process\b|RlWorkerProcess\.(?:RunAsync|PumpAsync)|TrainingPythonTestRunner\.RunAsync|\bRunWorkerAsync\b|RlPortfolioPythonScript\.Run\w*Async|\[(?:\w+\.)?(?:SmokeFact|AblationFact|ChildProcessFact|ChildProcessTheory)\]|\bnew\s+TrainingOrchestrator\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex ClassDeclaration = new(@"\bclass\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

    private static string SourceDirectory()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, ProjectDirectory, ProjectFile)))
            directory = Path.GetDirectoryName(directory);
        return directory is null
            ? throw new DirectoryNotFoundException("The sources of " + ProjectFile + " were not found above " + AppContext.BaseDirectory)
            : Path.Combine(directory, ProjectDirectory);
    }

    private static bool IsBuildOutput(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string WithoutComments(string source)
        => string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static bool HasTests(Type type)
        => type.GetMethods().Any(method => method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0);

    /// <summary>Top-level test classes declared in a file that calls a process starter, found through the sources and resolved by reflection.</summary>
    private static IReadOnlyList<Type> ProcessStartingTestClasses()
    {
        string self = nameof(ChildProcessCollectionGuardTests) + ".cs";
        var byName = typeof(ChildProcessCollectionGuardTests).Assembly.GetTypes()
            .Where(type => type.DeclaringType is null && HasTests(type)).GroupBy(type => type.Name).ToDictionary(g => g.Key, g => g.First());
        var found = new Dictionary<string, Type>();
        foreach (string file in Directory.EnumerateFiles(SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(Path.GetRelativePath(SourceDirectory(), file)) || Path.GetFileName(file) == self) continue;
            string source = WithoutComments(File.ReadAllText(file));
            if (!ProcessStarters.IsMatch(source)) continue;
            foreach (Match declaration in ClassDeclaration.Matches(source))
                if (byName.TryGetValue(declaration.Groups[1].Value, out var type)) found[type.FullName!] = type;
        }
        return found.Values.OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
    }

    /// <summary>The collections of this assembly that are defined with <c>DisableParallelization = true</c>: a test in one of them never runs beside another test.</summary>
    private static HashSet<string> SerialCollections()
        => typeof(ChildProcessCollectionGuardTests).Assembly.GetTypes().SelectMany(type => type.CustomAttributes)
            .Where(attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute) && attribute.ConstructorArguments.Count == 1
                && attribute.NamedArguments.Any(named => named.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization) && named.TypedValue.Value is true))
            .Select(attribute => (string)attribute.ConstructorArguments[0].Value!).ToHashSet(StringComparer.Ordinal);

    private static bool RunsAlone(Type type, HashSet<string> serialCollections)
        => type.CustomAttributes.Any(attribute => attribute.AttributeType == typeof(CollectionAttribute)
            && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is string name && serialCollections.Contains(name));

    [Fact]
    public void EveryTestClassThatStartsAChildProcessRunsInTheChildProcessCollectionOrAnotherSerialOne()
    {
        var serial = SerialCollections();
        Assert.Contains(CollectionName, serial);                                                  // the guard's own premise
        var violations = ProcessStartingTestClasses().Where(type => !RunsAlone(type, serial)).Select(type => type.FullName).ToList();
        Assert.True(violations.Count == 0,
            "These test classes start a child process but are not in [Collection(\"" + CollectionName + "\")] or another collection with disabled parallelization: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void TheScanFindsTheClassesThatAreKnownToStartChildProcesses()
    {
        // Guards the guard: a broken path or pattern would find nothing and pass without checking anything.
        var names = ProcessStartingTestClasses().Select(type => type.Name).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "RlWorkerProcessTests", "RlGenerationAndQValueTests", "RlCheckpointSafetyTests", "RlPortfolioPolicySelfCheckTests", "PreparedSourcePipelineParityTests",
            "RlPortfolioAblationHarnessTests", "RlPortfolioTrainingSmokeTests", "RlPortfolioTransferTests", "RlPortfolioServiceTests",
        }, names);
    }
}
