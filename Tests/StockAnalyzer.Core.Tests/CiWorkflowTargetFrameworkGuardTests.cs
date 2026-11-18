using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StockAnalyzer.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Guards <c>.github/workflows/ci.yml</c> against a step that restores, builds or runs a Windows-only test project (a target framework that contains
/// "-windows", for example <c>net8.0-windows</c> with WPF) on every operating system of the matrix. Such a step fails on ubuntu and macOS (NETSDK1100),
/// and the defect cannot be seen without reading the project file. Rules, per step of every job:
/// R1: a step whose <c>run</c> text names a Windows-only project carries the condition <c>runner.os == 'Windows'</c>;
/// R2: a step that names a Windows-only project names no other kind of project (the step is split instead).
/// The workflow itself runs only by hand (no Actions minutes), so this static check is the only automatic protection; the result of a real run on
/// ubuntu and macOS stays to be confirmed by the owner with workflow_dispatch.
/// </summary>
public sealed class CiWorkflowTargetFrameworkGuardTests
{
    private const string WorkflowRelativePath = ".github/workflows/ci.yml";
    private const string WindowsCondition = "runner.os == 'Windows'";
    private const string WindowsFrameworkMarker = "-windows";
    private const string CrossPlatformFramework = "net8.0";
    private const string WindowsFramework = "net8.0-windows";
    private const string WindowsOnlyNameMarker = "WindowsOnly";
    private const string R1 = "R1";
    private const string R2 = "R2";

    private static readonly Regex ProjectPath = new(@"[A-Za-z0-9_./\\-]+\.csproj", RegexOptions.Compiled);

    // The two layouts below are the ci.yml of commit 09b711dff (one step names a cross-platform and a Windows-only project, without the condition)
    // and the layout after the fix. The project names stand for the kind: the lookup of the self-test reads the marker from the name.
    private const string MixedStepsWithoutCondition = """
        jobs:
          test:
            steps:
              - name: Restore
                run: |
                  dotnet restore Tests/Cross.csproj --force
                  dotnet restore Tests/WindowsOnly.csproj --force
        """;

    private const string SplitStepsWithCondition = """
        jobs:
          test:
            steps:
              - name: Restore
                run: dotnet restore Tests/Cross.csproj --force
              - name: Restore Windows-only
                if: runner.os == 'Windows'
                run: dotnet restore Tests/WindowsOnly.csproj --force
        """;

    private const string WindowsStepWithWrongCondition = """
        jobs:
          test:
            steps:
              - name: Restore Windows-only
                if: runner.os != 'Linux'
                run: dotnet restore Tests/WindowsOnly.csproj --force
        """;

    private static string SelfTestFramework(string project)
        => project.Contains(WindowsOnlyNameMarker, StringComparison.Ordinal) ? WindowsFramework : CrossPlatformFramework;

    [Fact]
    public void TheWorkflowRunsEveryWindowsOnlyProjectOnlyOnWindows()
    {
        string root = TestRepositoryRoot.Find();
        string yaml = File.ReadAllText(Path.Combine(root, WorkflowRelativePath));

        IReadOnlyList<string> violations = FindViolations(yaml, project => FrameworkOfProjectFile(root, project));

        Assert.True(violations.Count == 0, WorkflowRelativePath + ": " + string.Join("; ", violations));
    }

    [Fact]
    public void AStepThatNamesBothKindsOfProjectWithoutTheConditionIsReportedForRulesR1AndR2()
    {
        IReadOnlyList<string> violations = FindViolations(MixedStepsWithoutCondition, SelfTestFramework);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, violation => violation.StartsWith(R1, StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.StartsWith(R2, StringComparison.Ordinal));
    }

    [Fact]
    public void TheSplitLayoutWithTheConditionHasNoViolation()
    {
        Assert.Empty(FindViolations(SplitStepsWithCondition, SelfTestFramework));
    }

    [Fact]
    public void AWindowsOnlyStepWithAnyOtherConditionIsReportedForRuleR1()
    {
        IReadOnlyList<string> violations = FindViolations(WindowsStepWithWrongCondition, SelfTestFramework);

        string only = Assert.Single(violations);
        Assert.StartsWith(R1, only, StringComparison.Ordinal);
    }

    /// <summary>The target framework (or the first of the target frameworks) of a project file below the repository root; a missing project fails the test.</summary>
    private static string FrameworkOfProjectFile(string root, string project)
    {
        string path = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The workflow names a project that does not exist: " + project, path);
        }

        XElement[] properties = XDocument.Load(path).Descendants().Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToArray();
        string value = properties.Select(element => element.Value.Trim()).FirstOrDefault(text => text.Length > 0)
            ?? throw new InvalidDataException("The project has no target framework: " + project);
        return value.Split(';')[0];
    }

    /// <summary>One entry per broken rule per step: "R1 job 'x' step 'y': ..." (a step breaks each rule at most once).</summary>
    private static IReadOnlyList<string> FindViolations(string workflowYaml, Func<string, string> frameworkOf)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflowYaml));
        var violations = new List<string>();
        var jobs = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
        foreach (KeyValuePair<YamlNode, YamlNode> job in jobs.Children)
        {
            if (job.Value is not YamlMappingNode jobBody || !jobBody.Children.TryGetValue(new YamlScalarNode("steps"), out YamlNode? stepsNode))
            {
                continue;
            }

            foreach (YamlMappingNode step in ((YamlSequenceNode)stepsNode).Children.OfType<YamlMappingNode>())
            {
                string? run = ScalarOf(step, "run");
                if (run is null)
                {
                    continue;
                }

                string[] projects = ProjectPath.Matches(run).Select(match => match.Value.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToArray();
                string[] windowsOnly = projects.Where(project => frameworkOf(project).Contains(WindowsFrameworkMarker, StringComparison.Ordinal)).ToArray();
                if (windowsOnly.Length == 0)
                {
                    continue;
                }

                string where = $"job '{job.Key}' step '{ScalarOf(step, "name") ?? "(unnamed)"}'";
                if (!string.Equals(ScalarOf(step, "if")?.Trim(), WindowsCondition, StringComparison.Ordinal))
                {
                    violations.Add($"{R1} {where}: names Windows-only {string.Join(", ", windowsOnly)} without the condition \"{WindowsCondition}\"");
                }

                string[] others = projects.Except(windowsOnly, StringComparer.Ordinal).ToArray();
                if (others.Length > 0)
                {
                    violations.Add($"{R2} {where}: names Windows-only {string.Join(", ", windowsOnly)} together with {string.Join(", ", others)}");
                }
            }
        }

        return violations;
    }

    private static string? ScalarOf(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) && node is YamlScalarNode scalar ? scalar.Value : null;
}
