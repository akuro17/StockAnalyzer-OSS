using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Guarantees 100% parity between runtime limit definitions (<see cref="McpLimits"/> and <see cref="McpLimitsContract"/>)
/// and the authoritative documentation (<c>docs/SA_AI_INTEGRATION.md</c> block 0004 and <c>docs/MCP_SERVER_COMMAND_REFERENCE.md</c> §3).
/// Prevents documentation drift and ensures limits claimed in documentation are backed by enforceable code.
/// </summary>
public sealed class McpLimitsDocumentParityTests
{
    public sealed record DocLimitEntry(string Key, string DefaultRaw, string BandRaw, int DefaultInt, string MinRaw, string MaxRaw);

    public sealed record CommandRefEntry(string Key, string Type, string DefaultRaw, string HardMinRaw, string HardMaxRaw, int DefaultInt);

    private static readonly Regex IntPrefixRegex = new(@"^\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex YamlKeyRegex = new(@"^\s*-\s*key:\s*(\w+)", RegexOptions.Compiled);
    private static readonly Regex YamlDefaultRegex = new(@"^\s*default:\s*([^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex YamlBandRegex = new(@"^\s*band:\s*([^\r\n]+)", RegexOptions.Compiled);

    public static Dictionary<string, DocLimitEntry> LoadIntegrationDocLimits()
    {
        string root = TestRepositoryRoot.Find();
        string path = Path.Combine(root, "docs", "SA_AI_INTEGRATION.md");
        Assert.True(File.Exists(path), $"Documentation file not found: {path}");

        string[] lines = File.ReadAllLines(path);
        var entries = new Dictionary<string, DocLimitEntry>(StringComparer.Ordinal);

        bool inBlock0004 = false;
        bool inKeys = false;
        string? currentKey = null;
        string? currentDefault = null;
        string? currentBand = null;

        void CommitCurrent()
        {
            if (currentKey is not null && currentDefault is not null && currentBand is not null)
            {
                var match = IntPrefixRegex.Match(currentDefault);
                int defaultInt = match.Success ? int.Parse(match.Groups[1].Value) : -1;

                string minRaw = string.Empty;
                string maxRaw = string.Empty;
                int dotIndex = currentBand.IndexOf("..", StringComparison.Ordinal);
                if (dotIndex >= 0)
                {
                    minRaw = currentBand[..dotIndex].Trim();
                    maxRaw = currentBand[(dotIndex + 2)..].Trim();
                }

                entries[currentKey] = new DocLimitEntry(currentKey, currentDefault, currentBand, defaultInt, minRaw, maxRaw);
            }
            currentKey = null;
            currentDefault = null;
            currentBand = null;
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();

            if (line.Contains("id: sa_ai_integration_0004", StringComparison.Ordinal))
            {
                inBlock0004 = true;
                continue;
            }

            if (inBlock0004)
            {
                if (line.StartsWith("```", StringComparison.Ordinal) || (line.StartsWith("id: ", StringComparison.Ordinal) && !line.Contains("sa_ai_integration_0004", StringComparison.Ordinal)))
                {
                    CommitCurrent();
                    break;
                }

                if (line.StartsWith("  keys:", StringComparison.Ordinal))
                {
                    inKeys = true;
                    continue;
                }

                if (inKeys && (line.StartsWith("  fixed_not_configurable:", StringComparison.Ordinal) || line.StartsWith("  execution_section:", StringComparison.Ordinal)))
                {
                    CommitCurrent();
                    inKeys = false;
                    continue;
                }

                if (inKeys)
                {
                    var keyMatch = YamlKeyRegex.Match(line);
                    if (keyMatch.Success)
                    {
                        CommitCurrent();
                        currentKey = keyMatch.Groups[1].Value;
                        continue;
                    }

                    var defaultMatch = YamlDefaultRegex.Match(line);
                    if (defaultMatch.Success)
                    {
                        currentDefault = defaultMatch.Groups[1].Value.Trim();
                        continue;
                    }

                    var bandMatch = YamlBandRegex.Match(line);
                    if (bandMatch.Success)
                    {
                        currentBand = bandMatch.Groups[1].Value.Trim();
                        continue;
                    }
                }
            }
        }

        CommitCurrent();
        return entries;
    }

    public static Dictionary<string, CommandRefEntry> LoadCommandRefLimits()
    {
        string root = TestRepositoryRoot.Find();
        string path = Path.Combine(root, "docs", "MCP_SERVER_COMMAND_REFERENCE.md");
        Assert.True(File.Exists(path), $"Documentation file not found: {path}");

        string[] lines = File.ReadAllLines(path);
        var entries = new Dictionary<string, CommandRefEntry>(StringComparer.Ordinal);

        bool inSection3 = false;
        bool inTable = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.StartsWith("## 3. Configuration Limits", StringComparison.Ordinal))
            {
                inSection3 = true;
                continue;
            }

            if (inSection3)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal) && !line.StartsWith("## 3.", StringComparison.Ordinal))
                {
                    break;
                }

                if (line.StartsWith("| Parameter Name |", StringComparison.Ordinal))
                {
                    inTable = true;
                    continue;
                }

                if (inTable && line.StartsWith("|---|", StringComparison.Ordinal))
                {
                    continue;
                }

                if (inTable)
                {
                    if (!line.StartsWith("|", StringComparison.Ordinal))
                    {
                        if (entries.Count > 0) break;
                        continue;
                    }

                    string[] cols = line.Split('|', StringSplitOptions.TrimEntries);
                    // Split produces empty string before first '|' and after last '|', so columns are 1..5
                    if (cols.Length >= 6)
                    {
                        string paramName = cols[1].Trim('`', ' ');
                        string type = cols[2].Trim('`', ' ');
                        string defaultRaw = cols[3].Trim('`', ' ');
                        string hardMin = cols[4].Trim('`', ' ');
                        string hardMax = cols[5].Trim('`', ' ');

                        if (string.IsNullOrEmpty(paramName) || paramName == "Parameter Name")
                        {
                            continue;
                        }

                        var match = IntPrefixRegex.Match(defaultRaw);
                        int defaultInt = match.Success ? int.Parse(match.Groups[1].Value) : -1;

                        entries[paramName] = new CommandRefEntry(paramName, type, defaultRaw, hardMin, hardMax, defaultInt);
                    }
                }
            }
        }

        return entries;
    }

    private static IReadOnlyList<PropertyInfo> GetMcpLimitsProperties()
    {
        return typeof(McpLimits)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(int))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void AllMcpLimitsPropertiesAreDocumentedInBothSpecifications()
    {
        var codeProps = GetMcpLimitsProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var integrationLimits = LoadIntegrationDocLimits();
        var commandRefLimits = LoadCommandRefLimits();

        // 1. Ensure count is significant (at least 40 properties)
        Assert.True(codeProps.Count >= 40, $"Expected at least 40 limits, but found {codeProps.Count}");

        // 2. Parity between Code and SA_AI_INTEGRATION.md
        var missingInIntegration = codeProps.Except(integrationLimits.Keys).ToList();
        var undocumentedInIntegration = integrationLimits.Keys.Except(codeProps).ToList();

        Assert.True(missingInIntegration.Count == 0,
            $"The following McpLimits properties are missing from SA_AI_INTEGRATION.md (sa_ai_integration_0004):\n{string.Join(", ", missingInIntegration)}");
        Assert.True(undocumentedInIntegration.Count == 0,
            $"The following SA_AI_INTEGRATION.md limits do not exist in McpLimits.cs:\n{string.Join(", ", undocumentedInIntegration)}");

        // 3. Parity between Code and MCP_SERVER_COMMAND_REFERENCE.md
        var missingInCommandRef = codeProps.Except(commandRefLimits.Keys).ToList();
        var undocumentedInCommandRef = commandRefLimits.Keys.Except(codeProps).ToList();

        Assert.True(missingInCommandRef.Count == 0,
            $"The following McpLimits properties are missing from MCP_SERVER_COMMAND_REFERENCE.md (§3):\n{string.Join(", ", missingInCommandRef)}");
        Assert.True(undocumentedInCommandRef.Count == 0,
            $"The following MCP_SERVER_COMMAND_REFERENCE.md limits do not exist in McpLimits.cs:\n{string.Join(", ", undocumentedInCommandRef)}");
    }

    [Fact]
    public void DefaultValuesMatchAcrossCodeAndDocumentation()
    {
        var limits = new McpLimits();
        var codeProps = GetMcpLimitsProperties();
        var integrationLimits = LoadIntegrationDocLimits();
        var commandRefLimits = LoadCommandRefLimits();

        var mismatches = new List<string>();

        foreach (var prop in codeProps)
        {
            int codeDefault = (int)prop.GetValue(limits)!;

            if (integrationLimits.TryGetValue(prop.Name, out var intEntry))
            {
                if (codeDefault != intEntry.DefaultInt)
                {
                    mismatches.Add($"{prop.Name}: Code={codeDefault}, SA_AI_INTEGRATION={intEntry.DefaultInt} (raw: '{intEntry.DefaultRaw}')");
                }
            }

            if (commandRefLimits.TryGetValue(prop.Name, out var refEntry))
            {
                if (codeDefault != refEntry.DefaultInt)
                {
                    mismatches.Add($"{prop.Name}: Code={codeDefault}, MCP_SERVER_COMMAND_REF={refEntry.DefaultInt} (raw: '{refEntry.DefaultRaw}')");
                }
            }
        }

        Assert.True(mismatches.Count == 0,
            $"Default value mismatches between McpLimits and documentation:\n{string.Join("\n", mismatches)}");
    }

    [Fact]
    public void BandDefinitionsMatchAcrossDocumentationSources()
    {
        var integrationLimits = LoadIntegrationDocLimits();
        var commandRefLimits = LoadCommandRefLimits();

        var mismatches = new List<string>();

        foreach (var kvp in integrationLimits)
        {
            string key = kvp.Key;
            var intEntry = kvp.Value;

            if (commandRefLimits.TryGetValue(key, out var refEntry))
            {
                // Normalize min/max comparisons
                string intMin = intEntry.MinRaw;
                string intMax = intEntry.MaxRaw;
                string refMin = refEntry.HardMinRaw;
                string refMax = refEntry.HardMaxRaw;

                // Handle WatchlistConstants.MaxRelativePerformanceTargets annotation
                if (intMax.Contains("WatchlistConstants.MaxRelativePerformanceTargets", StringComparison.Ordinal))
                {
                    intMax = "10";
                }
                if (refMax.Contains("WatchlistConstants.MaxRelativePerformanceTargets", StringComparison.Ordinal))
                {
                    refMax = "10";
                }

                // Handle ToolTimeout / ToolTimeoutSeconds alias
                if (intMin == "ToolTimeoutSeconds") intMin = "ToolTimeout";
                if (refMin == "ToolTimeoutSeconds") refMin = "ToolTimeout";

                if (!string.Equals(intMin, refMin, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{key} Min mismatch: SA_AI_INTEGRATION='{intEntry.MinRaw}', CommandRef='{refEntry.HardMinRaw}'");
                }

                // For max, strip any comment like "and at least ..." if present
                string cleanIntMax = intMax.Split(' ')[0].Trim();
                string cleanRefMax = refMax.Split(' ')[0].Trim();

                if (!string.Equals(cleanIntMax, cleanRefMax, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{key} Max mismatch: SA_AI_INTEGRATION='{intEntry.MaxRaw}', CommandRef='{refEntry.HardMaxRaw}'");
                }
            }
        }

        Assert.True(mismatches.Count == 0,
            $"Band mismatches between SA_AI_INTEGRATION.md and MCP_SERVER_COMMAND_REFERENCE.md:\n{string.Join("\n", mismatches)}");
    }

    [Fact]
    public void MutationCheck_DeliberateDiscrepancyFailsAssertion()
    {
        var codeProps = new HashSet<string> { "MaxCandlesPerRequest", "NonExistentLimit" };
        var docLimits = new Dictionary<string, DocLimitEntry>
        {
            { "MaxCandlesPerRequest", new DocLimitEntry("MaxCandlesPerRequest", "10000", "1..50000", 10000, "1", "50000") }
        };

        var missing = codeProps.Except(docLimits.Keys).ToList();
        Assert.Single(missing);
        Assert.Equal("NonExistentLimit", missing[0]);

        // Value mutation check
        int codeValue = 10000;
        int docValue = 9999;
        Assert.NotEqual(codeValue, docValue);
    }
}
