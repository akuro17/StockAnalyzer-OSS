using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The key table of <c>AiIntegration:Discussion</c> in <c>docs/SA_AI_INTEGRATION.md</c> (block 0004) is the same as <see cref="DiscussionSettings"/> and
/// <see cref="DiscussionLimits"/>: the same keys, the same defaults, the same hard bands and the same fixed values. The starter prompt of the command reference carries the
/// rule text of the server character for character.
/// </summary>
public sealed class DiscussionSettingsDocumentParityTests
{
    private static readonly Regex KeyRow = new(@"^\s*-\s*\{key:\s*(\w+),\s*default:\s*([^,]+),\s*band:\s*([^,]+),\s*use:", RegexOptions.Compiled);
    private static readonly Regex FixedRow = new(@"^\s*fixed:\s*\{(.+)\}\s*$", RegexOptions.Compiled);

    private sealed record Row(string Key, string Default, string Band);

    private static string[] Lines(string document)
        => File.ReadAllLines(Path.Combine(TestRepositoryRoot.Find(), "docs", document));

    private static (List<Row> Keys, Dictionary<string, string> Fixed) LoadDocumentedTable()
    {
        string[] lines = Lines("SA_AI_INTEGRATION.md");
        int start = Array.FindIndex(lines, line => line.Trim() == "discussion_section:");
        Assert.True(start >= 0, "discussion_section not found in block 0004");
        var keys = new List<Row>();
        var fixedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = start + 1; i < lines.Length; i++)
        {
            Match key = KeyRow.Match(lines[i]);
            if (key.Success)
            {
                keys.Add(new Row(key.Groups[1].Value, key.Groups[2].Value.Trim(), key.Groups[3].Value.Trim()));
                continue;
            }

            Match fixedRow = FixedRow.Match(lines[i]);
            if (fixedRow.Success && lines[i].TrimStart().StartsWith("fixed:", StringComparison.Ordinal))
            {
                foreach (string pair in fixedRow.Groups[1].Value.Split(','))
                {
                    string[] parts = pair.Split(':');
                    fixedValues[parts[0].Trim()] = parts[1].Trim();
                }

                break;
            }
        }

        return (keys, fixedValues);
    }

    private static readonly Dictionary<string, (long Minimum, long Maximum)> Bands = new(StringComparer.Ordinal)
    {
        [nameof(DiscussionSettings.MaxActiveSessions)] = Band(DiscussionLimits.ActiveSessionsBand),
        [nameof(DiscussionSettings.MaxParticipantsPerSession)] = Band(DiscussionLimits.ParticipantsBand),
        [nameof(DiscussionSettings.MaxMessagesPerSession)] = Band(DiscussionLimits.MessagesBand),
        [nameof(DiscussionSettings.MaxMessageBytes)] = Band(DiscussionLimits.MessageBytesBand),
        [nameof(DiscussionSettings.MaxContextsPerSession)] = Band(DiscussionLimits.ContextsBand),
        [nameof(DiscussionSettings.MaxContextBytes)] = Band(DiscussionLimits.ContextBytesBand),
        [nameof(DiscussionSettings.MaxTotalBytes)] = (DiscussionLimits.MinTotalBytes, DiscussionLimits.MaxTotalBytesAllowed),
        [nameof(DiscussionSettings.IdleTimeoutSeconds)] = Band(DiscussionLimits.IdleTimeoutBand),
        [nameof(DiscussionSettings.MaxSessionAgeSeconds)] = Band(DiscussionLimits.MaxSessionAgeBand),
        [nameof(DiscussionSettings.ClosedRetentionSeconds)] = Band(DiscussionLimits.ClosedRetentionBand),
        [nameof(DiscussionSettings.AwaySeconds)] = Band(DiscussionLimits.AwayBand),
        [nameof(DiscussionSettings.FloorTimeoutSeconds)] = Band(DiscussionLimits.FloorTimeoutBand),
        [nameof(DiscussionSettings.MaxWaitSeconds)] = Band(DiscussionLimits.WaitBand),
        [nameof(DiscussionSettings.MaxPostsPerMinute)] = Band(DiscussionLimits.PostsPerMinuteBand),
        [nameof(DiscussionSettings.MaxReadLimit)] = Band(DiscussionLimits.MaxReadLimitBand),
        [nameof(DiscussionSettings.AuditCapacity)] = Band(DiscussionLimits.AuditCapacityBand),
        [nameof(DiscussionSettings.CommandLogCapacity)] = Band(DiscussionLimits.CommandLogCapacityBand),
        [nameof(DiscussionSettings.CommandLogMaxFileBytes)] = Band(DiscussionLimits.CommandLogFileBytesBand),
        [nameof(DiscussionSettings.CommandLogGenerations)] = Band(DiscussionLimits.CommandLogGenerationsBand),
        [nameof(DiscussionSettings.CommandLogMaxAgeHours)] = Band(DiscussionLimits.CommandLogMaxAgeHoursBand),
    };

    private static (long, long) Band(DiscussionBand band) => (band.Minimum, band.Maximum);

    [Fact]
    public void TheDocumentedKeysAreExactlyThePropertiesOfTheSettings()
    {
        (List<Row> keys, _) = LoadDocumentedTable();

        string[] properties = typeof(DiscussionSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();

        Assert.Equal(properties.OrderBy(n => n, StringComparer.Ordinal), keys.Select(k => k.Key).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(keys.Count, keys.Select(k => k.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryDocumentedDefaultIsTheDefaultOfTheSetting()
    {
        (List<Row> keys, _) = LoadDocumentedTable();
        var settings = new DiscussionSettings();

        foreach (Row row in keys)
        {
            object value = typeof(DiscussionSettings).GetProperty(row.Key)!.GetValue(settings)!;
            Assert.True(
                string.Equals(row.Default, Convert.ToString(value, CultureInfo.InvariantCulture)?.ToLowerInvariant(), StringComparison.Ordinal),
                $"{row.Key}: the document says {row.Default}, the code {value}");
        }
    }

    [Fact]
    public void EveryDocumentedBandIsTheHardBandOfTheCode()
    {
        (List<Row> keys, _) = LoadDocumentedTable();

        foreach (Row row in keys)
        {
            if (row.Key == nameof(DiscussionSettings.CommandLogToFile))
            {
                Assert.Equal("bool", row.Band);
            }
            else if (row.Key == nameof(DiscussionSettings.DefaultReadLimit))
            {
                Assert.Equal($"{DiscussionLimits.MinDefaultReadLimit}..{nameof(DiscussionSettings.MaxReadLimit)}", row.Band);
            }
            else
            {
                (long minimum, long maximum) = Bands[row.Key];
                Assert.True(string.Equals($"{minimum}..{maximum}", row.Band, StringComparison.Ordinal), $"{row.Key}: the document says {row.Band}, the code {minimum}..{maximum}");
            }
        }

        Assert.Equal(Bands.Count + 2, keys.Count); // the two keys that have no plain band are handled above
    }

    [Fact]
    public void TheDocumentedFixedValuesAreTheConstantsOfTheCode()
    {
        (_, Dictionary<string, string> fixedValues) = LoadDocumentedTable();

        var expected = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["wire_version"] = DiscussionLimits.WireVersion,
            ["title_max_chars"] = DiscussionLimits.TitleMaxChars,
            ["topic_max_chars"] = DiscussionLimits.TopicMaxChars,
            ["display_name_max_chars"] = DiscussionLimits.DisplayNameMaxChars,
            ["client_request_id_max_chars"] = DiscussionLimits.ClientRequestIdMaxChars,
            ["idempotency_remembered"] = DiscussionLimits.IdempotencyRemembered,
            ["note_max_chars"] = DiscussionLimits.NoteMaxChars,
            ["analysis_arguments_max_bytes"] = DiscussionLimits.AnalysisArgumentsMaxBytes,
            ["max_context_ids_per_post"] = DiscussionLimits.MaxContextIdsPerPost,
            ["status_tail_max"] = DiscussionLimits.StatusTailMax,
            ["command_log_text_max_chars"] = DiscussionLimits.CommandLogTextMaxChars,
            ["command_log_max_arg_keys"] = DiscussionLimits.CommandLogMaxArgKeys,
            ["sweep_interval_seconds"] = DiscussionLimits.SweepIntervalSeconds,
        };

        Assert.Equal(expected.Keys.OrderBy(n => n, StringComparer.Ordinal), fixedValues.Keys.OrderBy(n => n, StringComparer.Ordinal));
        foreach ((string name, long value) in expected)
        {
            Assert.Equal(value.ToString(CultureInfo.InvariantCulture), fixedValues[name]);
        }
    }

    [Fact]
    public void TheStarterPromptOfTheCommandReferenceContainsTheServersRuleTextCharacterForCharacter()
    {
        string reference = File.ReadAllText(Path.Combine(TestRepositoryRoot.Find(), "docs", "MCP_SERVER_COMMAND_REFERENCE.md"));

        Assert.Contains(DiscussionProtocolText.Text, reference, StringComparison.Ordinal);
        Assert.Contains("```text", reference, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandReferenceNamesEveryDiscussionLimitKeyItQuotesAndTheRegistrationExamples()
    {
        string reference = File.ReadAllText(Path.Combine(TestRepositoryRoot.Find(), "docs", "MCP_SERVER_COMMAND_REFERENCE.md"));

        Assert.Contains("\"args\":[\"--enable-discussion\"]", reference, StringComparison.Ordinal);
        foreach (string key in new[] { "MaxWaitSeconds", "MaxReadLimit", "DefaultReadLimit", "MaxPostsPerMinute", "FloorTimeoutSeconds", "ClosedRetentionSeconds", "MaxMessageBytes", "MaxContextBytes", "CommandLogToFile" })
        {
            Assert.Contains(key, reference, StringComparison.Ordinal);
            Assert.NotNull(typeof(DiscussionSettings).GetProperty(key));
        }
    }
}
