#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// Task T10: the standard values of <see cref="DiscussionSettings"/> were chosen to be consistent with each other; these tests measure them. Each test writes its figures to
/// the test output (the figures in <c>SA_AI_INTEGRATION.md</c>, block 0018 <c>measurements</c>, come from here) and assert nothing about time or process memory, because both depend on what else runs at the same time (a full run showed a first election of 4.3 s instead of 0.3 s under load); they assert only what is exact (byte counters, the size of a result on the link). A slow machine does
/// (a result that no longer fits the link, a byte counter that drifts) is still caught.
/// </summary>
public sealed class DiscussionMeasurementTests : IDisposable
{
    private static readonly string DataFolder = Path.Combine(Path.GetTempPath(), "dhub-measure-data");

    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _disposables = new();

    public DiscussionMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private static string Percentile(List<double> milliseconds, double fraction)
    {
        double[] sorted = milliseconds.OrderBy(m => m).ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * fraction))].ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    }


    // ---- Memory ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AllFourActiveDiscussionsFilledToTheirLimitsStayInsideThePayloadBudgetAndTheCounterIsExact()
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        using var rig = new HubRig();
        DiscussionSettings settings = rig.Settings;
        var sessions = new List<(string Id, string[] Tokens)>();
        for (int s = 0; s < settings.MaxActiveSessions; s++)
        {
            string id = rig.NewSession($"full {s}");
            string[] tokens = Enumerable.Range(0, settings.MaxParticipantsPerSession).Select(p => rig.Join(id, $"agent {p}").Token).ToArray();
            sessions.Add((id, tokens));
        }

        // Contexts first (the biggest payload), then messages up to the cap of the session; time moves so the rate limit never refuses.
        for (int s = 0; s < sessions.Count; s++)
        {
            for (int c = 0; c < settings.MaxContextsPerSession; c++)
            {
                string result = "{\"v\":\"" + new string((char)('a' + (c % 26)), settings.MaxContextBytes - 20) + $"{s:D2}{c:D3}\"}}";
                rig.Time.Advance(4);
                rig.Hub.AttachCommit(sessions[s].Id, sessions[s].Tokens[0], "sa_calculate_indicators", $"{{\"n\":{s * 100 + c}}}", result, null);
            }
        }

        string content = new('x', settings.MaxMessageBytes);
        bool full = false;
        for (int m = 0; !full; m++)
        {
            foreach ((string id, string[] tokens) in sessions)
            {
                rig.Time.Advance(4);
                try
                {
                    rig.Post(id, tokens[m % tokens.Length], content);
                }
                catch (DiscussionException error) when (error.Code == DiscussionErrorCodes.LimitExceeded)
                {
                    full = true;
                }
            }
        }

        DiscussionDiagnostics diagnostics = rig.Hub.GetDiagnostics();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long managed = after - before;
        double factor = (double)managed / diagnostics.TotalBytes;
        _output.WriteLine($"payload (TotalBytes) = {diagnostics.TotalBytes / 1048576.0:F1} MiB of the {settings.MaxTotalBytes / 1048576} MiB budget; managed memory = {managed / 1048576.0:F1} MiB; factor = {factor:F2}; sessions = {diagnostics.SessionCount}");

        Assert.Equal(diagnostics.TotalBytes, diagnostics.RecomputedBytes);
        Assert.True(diagnostics.TotalBytes <= settings.MaxTotalBytes);
        // Not asserted: the managed memory delta also counts what other tests allocate at the same time. The figure is for the documentation only.
        GC.KeepAlive(rig);
    }

    // ---- Time -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APostAndAReadOfAFilledDiscussionAreMilliseconds()
    {
        using var rig = new HubRig(s => s.MaxPostsPerMinute = 120);
        string id = rig.NewSession();
        string[] tokens = Enumerable.Range(0, 6).Select(p => rig.Join(id, $"agent {p}").Token).ToArray();
        string content = new string('x', 1024);
        var post = new List<double>();
        var read = new List<double>();
        for (int i = 0; i < 280; i++)
        {
            rig.Time.Advance(0.1);
            long start = Stopwatch.GetTimestamp();
            rig.Post(id, tokens[i % 6], content);
            post.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        for (int i = 0; i < 280; i++)
        {
            long start = Stopwatch.GetTimestamp();
            DiscussionReadResult page = await rig.Hub.ReadAsync(id, tokens[i % 6], 0, rig.Settings.DefaultReadLimit);
            read.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Assert.Equal(rig.Settings.DefaultReadLimit, page.Messages.Count);
        }

        _output.WriteLine($"in-process post: median {Percentile(post, 0.5)} ms, p95 {Percentile(post, 0.95)} ms, max {Percentile(post, 0.9999)} ms");
        _output.WriteLine($"in-process read of {rig.Settings.DefaultReadLimit} messages: median {Percentile(read, 0.5)} ms, p95 {Percentile(read, 0.95)} ms");
    }

    [Fact]
    public async Task ACallThroughThePipeCostsMillisecondsAndTheFirstElectionAboutTheConnectTime()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        var settings = new DiscussionSettings { MaxPostsPerMinute = 120 };
        DiscussionGatewayRouter hub = NewRouter(suffix, settings);
        DiscussionGatewayRouter remote = NewRouter(suffix, settings);

        var clock = Stopwatch.StartNew();
        using JsonDocument created = await Ok(hub, DiscussionOperations.Create, """{"title":"latency"}""");
        double firstElection = clock.Elapsed.TotalMilliseconds;
        string id = created.RootElement.GetProperty("sessionId").GetString()!;
        clock.Restart();
        using JsonDocument joined = await Ok(remote, DiscussionOperations.Join, $$"""{"sessionId":"{{id}}","displayName":"remote"}""");
        double firstRemote = clock.Elapsed.TotalMilliseconds;
        string token = joined.RootElement.GetProperty("participantToken").GetString()!;

        var posts = new List<double>();
        var reads = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            long start = Stopwatch.GetTimestamp();
            using JsonDocument posted = await Ok(remote, DiscussionOperations.Post, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","content":"message {{i}} with some text 日本語"}""");
            posts.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        for (int i = 0; i < 100; i++)
        {
            long start = Stopwatch.GetTimestamp();
            using JsonDocument page = await Ok(remote, DiscussionOperations.Read, $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","afterSequence":0,"limit":20}""");
            reads.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        _output.WriteLine($"first call of the process that finds no hub (election incl. probe wait) = {firstElection:F0} ms; first call of a process that finds the hub = {firstRemote:F0} ms");
        _output.WriteLine($"remote post: median {Percentile(posts, 0.5)} ms, p95 {Percentile(posts, 0.95)} ms; remote read of 20: median {Percentile(reads, 0.5)} ms, p95 {Percentile(reads, 0.95)} ms");
    }

    // ---- Size on the link -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ascii", 50)]
    [InlineData("japanese", 50)]
    [InlineData("emoji", 50)]
    [InlineData("emoji", 20)]
    public async Task AFullReadPageStaysInsideTheLinkAndTheSizeFactorIsKnown(string kind, int limit)
    {
        var settings = new DiscussionSettings();
        string unit = kind switch { "ascii" => "a", "japanese" => "あ", _ => "😀" };
        int unitBytes = Encoding.UTF8.GetByteCount(unit);
        string content = string.Concat(Enumerable.Repeat(unit, settings.MaxMessageBytes / unitBytes));
        Assert.True(Encoding.UTF8.GetByteCount(content) <= settings.MaxMessageBytes);

        using var rig = new HubRig(s => s.MaxPostsPerMinute = 120);
        var gateway = new DiscussionHubGateway(rig.Hub, rig.Settings);
        string id = rig.NewSession();
        string token = rig.Join(id, "writer").Token;
        for (int i = 0; i < limit; i++)
        {
            rig.Time.Advance(1);
            rig.Post(id, token, content);
        }

        DiscussionReply reply = await gateway.ExecuteAsync(
            new DiscussionRequest(DiscussionOperations.Read, "test", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}","afterSequence":0,"limit":{{limit}}}"""), CancellationToken.None);

        Assert.True(reply.IsOk);
        int resultBytes = Encoding.UTF8.GetByteCount(reply.ResultJson!);
        long payload = (long)limit * Encoding.UTF8.GetByteCount(content);
        int wireBytes = DiscussionWire.EncodeReply(reply).Length;
        _output.WriteLine($"{kind} x {limit}: payload {payload / 1024} KiB; result JSON {resultBytes / 1024} KiB ({(double)resultBytes / payload:F2}x); wire line {wireBytes / 1024} KiB of the {DiscussionLimits.WireMaxLineBytes / 1024} KiB limit");
        Assert.True(wireBytes < DiscussionLimits.WireMaxLineBytes, "the wire line");
    }

    [Fact]
    public async Task ASnapshotOfTheLargestSizeWithTheMostEscapingStillFitsOneWireLine()
    {
        var settings = new DiscussionSettings();
        // The worst case for the request line: the result text is JSON full of quotes, so it is escaped once more inside the string value of attach_commit.
        string result = "{\"v\":\"" + string.Concat(Enumerable.Repeat("\\\"", (settings.MaxContextBytes - 20) / 2)) + "\"}"; // every quote inside the result text is two bytes (backslash, quote)
        Assert.True(Encoding.UTF8.GetByteCount(result) <= settings.MaxContextBytes);
        string request = JsonSerializer.Serialize(new { sessionId = "Sabcdef", participantToken = "t", tool = "sa_calculate_indicators", arguments = new { n = 1 }, result });
        byte[] line = DiscussionWire.EncodeRequest(new DiscussionRequest(DiscussionOperations.AttachCommit, "test", request));
        _output.WriteLine($"snapshot result {Encoding.UTF8.GetByteCount(result) / 1024} KiB -> attach_commit request line {line.Length / 1024} KiB of the {DiscussionLimits.WireMaxLineBytes / 1024} KiB limit ({(double)line.Length / Encoding.UTF8.GetByteCount(result):F2}x)");

        Assert.True(line.Length < DiscussionLimits.WireMaxLineBytes);
        await Task.CompletedTask;
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------------------------------------

    private DiscussionGatewayRouter NewRouter(string suffix, DiscussionSettings settings)
    {
        var router = new DiscussionGatewayRouter(settings, TimeProvider.System, commandLog: null, protocolSuffix: null, DataFolder, new DiscussionTransportOptions(suffix));
        _disposables.Add(router);
        return router;
    }

    private static async Task<JsonDocument> Ok(IDiscussionGateway gateway, string operation, string arguments)
    {
        DiscussionReply reply = await gateway.ExecuteAsync(new DiscussionRequest(operation, "test", arguments), CancellationToken.None);
        Assert.True(reply.IsOk, reply.Failure?.Code + " " + reply.Failure?.Message);
        return JsonDocument.Parse(reply.ResultJson!);
    }
}
