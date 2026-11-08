using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Task T7 with real server processes (spec 5.12 and section 11): servers that read the same data folder share one board over the pipe, servers of another data folder do
/// not, and when the hub process ends the board is gone and an old token is a HUB_RESET. The data folder of each child is chosen with the UI-automation data root
/// the path resolver honours, so a test never meets the board of a server the developer is running.
/// </summary>
public sealed class DiscussionSharingProcessTests : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private readonly List<ServerProcess> _servers = new();
    private readonly List<string> _folders = new();

    public void Dispose()
    {
        foreach (ServerProcess server in _servers)
        {
            server.Dispose();
        }

        foreach (string folder in _folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string NewDataRoot()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dhub-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        return folder;
    }

    private string NewMarketDataRoot(params string[] tickers)
    {
        string folder = NewDataRoot();
        using var fixture = ReadOnlyDataTreeFixture.Create(100, tickers.Length > 0 ? tickers : new[] { "msft" });
        CopyDirectory(fixture.Root, folder);
        return folder;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    private static void WriteDiscussionConfig(string dataRoot, object discussionSettings)
    {
        string configDir = Path.Combine(dataRoot, "Config");
        Directory.CreateDirectory(configDir);
        string json = JsonSerializer.Serialize(new { AiIntegration = new { Discussion = discussionSettings } });
        File.WriteAllText(Path.Combine(configDir, "user_mcpserver_settings.json"), json);
    }

    private async Task<ServerProcess> StartAsync(
        string dataRoot,
        IReadOnlyDictionary<string, string?>? envOverrides = null,
        IReadOnlyList<string>? extraArgs = null)
    {
        var server = new ServerProcess(dataRoot, envOverrides, extraArgs);
        _servers.Add(server);
        await server.InitializeAsync();
        return server;
    }

    private sealed class ServerProcess : IDisposable
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private int _nextId = 1;

        public ServerProcess(
            string dataRoot,
            IReadOnlyDictionary<string, string?>? envOverrides = null,
            IReadOnlyList<string>? extraArgs = null)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(McpServerHostTests.LocateServerAssembly());
            startInfo.ArgumentList.Add("--enable-discussion");
            if (extraArgs != null)
            {
                foreach (string arg in extraArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }
            }
            startInfo.Environment[PathDiscovery.UiTestEnabledEnvironmentVariable] = "1";
            startInfo.Environment[PathDiscovery.UiTestDataRootEnvironmentVariable] = dataRoot;
            if (envOverrides != null)
            {
                foreach (KeyValuePair<string, string?> pair in envOverrides)
                {
                    if (pair.Value != null)
                    {
                        startInfo.Environment[pair.Key] = pair.Value;
                    }
                }
            }
            _process = Process.Start(startInfo)!;
            _process.StandardInput.NewLine = "\n";
            _ = _process.StandardError.ReadToEndAsync(); // never let a full error pipe block the server
        }

        public async Task InitializeAsync()
        {
            await SendAsync(McpTestSupport.InitializeMessage);
            await ReadResponseAsync(0);
        }

        private Task SendAsync(string line) => _process.StandardInput.WriteLineAsync(line);

        private async Task<JsonDocument> ReadResponseAsync(int id)
        {
            using var limit = new CancellationTokenSource(CallTimeout);
            while (true)
            {
                string line = await _process.StandardOutput.ReadLineAsync(limit.Token) ?? throw new InvalidOperationException("The server closed its output.");
                JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out JsonElement responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                {
                    return document;
                }

                document.Dispose();
            }
        }

        /// <summary>One tools/call; returns the structured result (or the error object) and whether the call failed.</summary>
        public async Task<(JsonDocument Response, bool IsError)> CallAsync(string tool, string argumentsJson = "{}")
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                int id = _nextId++;
                await SendAsync(McpTestSupport.CallMessage(id, tool, argumentsJson)).ConfigureAwait(false);
                JsonDocument response = await ReadResponseAsync(id).ConfigureAwait(false);
                return (response, McpTestSupport.IsError(response));
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task<JsonElement> OkAsync(List<JsonDocument> keep, string tool, string argumentsJson = "{}")
        {
            (JsonDocument response, bool isError) = await CallAsync(tool, argumentsJson);
            keep.Add(response);
            Assert.False(isError, response.RootElement.GetRawText());
            return McpTestSupport.Structured(response);
        }

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10000);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(10000))
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _ioLock.Dispose();
                _process.Dispose();
            }
        }
    }

    [Fact]
    public async Task ServersOfOneDataFolderShareABoardAnotherFolderHasItsOwnAndTheEndOfTheHubIsAHubReset()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the pipe hub is guaranteed on Windows only
        }

        var keep = new List<JsonDocument>();
        try
        {
            string sharedRoot = NewDataRoot();
            ServerProcess first = await StartAsync(sharedRoot);
            ServerProcess second = await StartAsync(sharedRoot);
            ServerProcess elsewhere = await StartAsync(NewDataRoot());

            // The first server to use the board becomes its hub; the second finds it.
            JsonElement created = await first.OkAsync(keep, "sa_discussion_create", """{"title":"two clients","topic":"chart analysis"}""");
            string sessionId = created.GetProperty("sessionId").GetString()!;
            JsonElement joinedFirst = await first.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Antigravity agent"}""");
            JsonElement joinedSecond = await second.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"VS Code agent"}""");
            string firstToken = joinedFirst.GetProperty("participantToken").GetString()!;
            string secondToken = joinedSecond.GetProperty("participantToken").GetString()!;
            long seen = joinedSecond.GetProperty("lastSequence").GetInt64();

            await second.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{secondToken}}","content":"RSI looks stretched 日本語"}""");
            JsonElement read = await first.OkAsync(keep, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{firstToken}}","afterSequence":0}""");

            Assert.Contains(
                read.GetProperty("messages").EnumerateArray(),
                message => message.GetProperty("content").GetString() == "RSI looks stretched 日本語");

            JsonElement statusFirst = await first.OkAsync(keep, "sa_discussion_status");
            JsonElement statusSecond = await second.OkAsync(keep, "sa_discussion_status");
            JsonElement statusElsewhere = await elsewhere.OkAsync(keep, "sa_discussion_status");

            Assert.Equal("hub", statusFirst.GetProperty("role").GetString());
            Assert.Equal("remote", statusSecond.GetProperty("role").GetString());
            Assert.True(statusFirst.GetProperty("sharing").GetBoolean());
            Assert.Equal(statusFirst.GetProperty("sharingKey").GetString(), statusSecond.GetProperty("sharingKey").GetString());
            Assert.Equal(statusFirst.GetProperty("hubEpoch").GetString(), statusSecond.GetProperty("hubEpoch").GetString());

            // The server of the other data folder holds a board of its own: empty, its own epoch and key.
            Assert.Equal("hub", statusElsewhere.GetProperty("role").GetString());
            Assert.Empty(statusElsewhere.GetProperty("sessions").EnumerateArray());
            Assert.NotEqual(statusFirst.GetProperty("sharingKey").GetString(), statusElsewhere.GetProperty("sharingKey").GetString());
            Assert.NotEqual(statusFirst.GetProperty("hubEpoch").GetString(), statusElsewhere.GetProperty("hubEpoch").GetString());

            // The hub process ends: the board is gone. The survivor elects itself on its next call and the old token is a HUB_RESET.
            first.Kill();
            JsonElement afterwards = await second.OkAsync(keep, "sa_discussion_status");
            Assert.Equal("hub", afterwards.GetProperty("role").GetString());
            Assert.NotEqual(statusFirst.GetProperty("hubEpoch").GetString(), afterwards.GetProperty("hubEpoch").GetString());
            Assert.Empty(afterwards.GetProperty("sessions").EnumerateArray());

            (JsonDocument reset, bool isError) = await second.CallAsync(
                "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{secondToken}}","afterSequence":{{seen}}}""");
            keep.Add(reset);
            Assert.True(isError);
            Assert.Equal("HUB_RESET", McpTestSupport.ErrorCodeOf(reset));
        }
        finally
        {
            foreach (JsonDocument document in keep)
            {
                document.Dispose();
            }
        }
    }

    [Fact]
    public async Task HostTerminationAndMultiSurvivorReElectionElectsSingleNewHubAndPreservesRepeatabilityContract()
    {
        var keep = new List<JsonDocument>();
        try
        {
            string sharedRoot = NewDataRoot();
            ServerProcess first = await StartAsync(sharedRoot);
            ServerProcess second = await StartAsync(sharedRoot);
            ServerProcess third = await StartAsync(sharedRoot);

            // Establish first as hub, others as remote
            JsonElement statusFirst = await first.OkAsync(keep, "sa_discussion_status");
            JsonElement statusSecond = await second.OkAsync(keep, "sa_discussion_status");
            JsonElement statusThird = await third.OkAsync(keep, "sa_discussion_status");

            Assert.Equal("hub", statusFirst.GetProperty("role").GetString());
            Assert.Equal("remote", statusSecond.GetProperty("role").GetString());
            Assert.Equal("remote", statusThird.GetProperty("role").GetString());

            string initialEpoch = statusFirst.GetProperty("hubEpoch").GetString()!;
            Assert.Equal(initialEpoch, statusSecond.GetProperty("hubEpoch").GetString());
            Assert.Equal(initialEpoch, statusThird.GetProperty("hubEpoch").GetString());

            // Create a session and join
            JsonElement created = await first.OkAsync(keep, "sa_discussion_create", """{"title":"failover session"}""");
            string sessionId = created.GetProperty("sessionId").GetString()!;
            JsonElement joinedSecond = await second.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Survivor2"}""");
            string secondToken = joinedSecond.GetProperty("participantToken").GetString()!;

            // Terminate hub
            first.Kill();

            // 1. Non-repeatable call (post without clientRequestId) on broken connection fails with HUB_UNAVAILABLE
            (JsonDocument postFail, bool postError) = await second.CallAsync(
                "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{secondToken}}","content":"post on broken pipe"}""");
            keep.Add(postFail);
            Assert.True(postError);
            Assert.Equal("HUB_UNAVAILABLE", McpTestSupport.ErrorCodeOf(postFail));

            // 2. Concurrent repeatable calls (status) trigger re-election; exactly one becomes hub, one remote, both share new epoch
            Task<(JsonDocument Doc, bool Error)> task2 = second.CallAsync("sa_discussion_status");
            Task<(JsonDocument Doc, bool Error)> task3 = third.CallAsync("sa_discussion_status");
            await Task.WhenAll(task2, task3);

            (JsonDocument doc2, bool err2) = await task2;
            (JsonDocument doc3, bool err3) = await task3;
            keep.Add(doc2);
            keep.Add(doc3);

            Assert.False(err2, doc2.RootElement.GetRawText());
            Assert.False(err3, doc3.RootElement.GetRawText());

            JsonElement res2 = McpTestSupport.Structured(doc2);
            JsonElement res3 = McpTestSupport.Structured(doc3);

            string newEpoch2 = res2.GetProperty("hubEpoch").GetString()!;
            string newEpoch3 = res3.GetProperty("hubEpoch").GetString()!;
            Assert.NotEqual(initialEpoch, newEpoch2);
            Assert.Equal(newEpoch2, newEpoch3);

            string role2 = res2.GetProperty("role").GetString()!;
            string role3 = res3.GetProperty("role").GetString()!;
            var roles = new HashSet<string> { role2, role3 };
            Assert.Contains("hub", roles);
            Assert.Contains("remote", roles);

            // 3. Repeatable call to previous session on new hub yields HUB_RESET
            (JsonDocument resetDoc, bool resetError) = await second.CallAsync(
                "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{secondToken}}","afterSequence":0}""");
            keep.Add(resetDoc);
            Assert.True(resetError);
            Assert.Equal("HUB_RESET", McpTestSupport.ErrorCodeOf(resetDoc));
        }
        finally
        {
            foreach (JsonDocument doc in keep)
            {
                doc.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentMultiProcessPostsAndReadsMaintainStrictSequenceAndTurnOrder()
    {
        var keep = new List<JsonDocument>();
        try
        {
            string sharedRoot = NewDataRoot();
            ServerProcess p1 = await StartAsync(sharedRoot);
            ServerProcess p2 = await StartAsync(sharedRoot);
            ServerProcess p3 = await StartAsync(sharedRoot);

            // Part A: 'free' turn policy concurrent burst with clientRequestId
            JsonElement created = await p1.OkAsync(keep, "sa_discussion_create", """{"title":"concurrent burst","turnMode":"free"}""");
            string sessionId = created.GetProperty("sessionId").GetString()!;

            JsonElement j1 = await p1.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Agent1"}""");
            JsonElement j2 = await p2.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Agent2"}""");
            JsonElement j3 = await p3.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Agent3"}""");

            string t1 = j1.GetProperty("participantToken").GetString()!;
            string t2 = j2.GetProperty("participantToken").GetString()!;
            string t3 = j3.GetProperty("participantToken").GetString()!;

            // 3 processes post 5 messages each in parallel (15 total)
            var postTasks = new List<Task<(JsonDocument Doc, bool Error)>>();
            for (int i = 1; i <= 5; i++)
            {
                postTasks.Add(p1.CallAsync("sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t1}}","content":"p1-{{i}}","clientRequestId":"p1-req-{{i}}"}"""));
                postTasks.Add(p2.CallAsync("sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","content":"p2-{{i}}","clientRequestId":"p2-req-{{i}}"}"""));
                postTasks.Add(p3.CallAsync("sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t3}}","content":"p3-{{i}}","clientRequestId":"p3-req-{{i}}"}"""));
            }

            var postResults = await Task.WhenAll(postTasks);
            foreach (var (doc, err) in postResults)
            {
                keep.Add(doc);
                Assert.False(err, doc.RootElement.GetRawText());
            }

            // Verify strict sequence numbers [1..18] (3 join event messages + 15 concurrent user posts)
            JsonElement read = await p1.OkAsync(keep, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t1}}","afterSequence":0}""");
            var messages = read.GetProperty("messages").EnumerateArray().ToList();
            Assert.Equal(18, messages.Count);
            for (int i = 0; i < 18; i++)
            {
                Assert.Equal(i + 1, messages[i].GetProperty("sequence").GetInt64());
            }

            // Deduplication test: replaying committed clientRequestId from p2 returns duplicate == true
            JsonElement dup = await p2.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","content":"p2-1","clientRequestId":"p2-req-1"}""");
            Assert.True(dup.GetProperty("duplicate").GetBoolean());

            // Part B: 'roundRobin' turn policy, pass, and leave
            JsonElement rrCreated = await p1.OkAsync(keep, "sa_discussion_create", """{"title":"round robin test","turnMode":"roundRobin"}""");
            string rrSessionId = rrCreated.GetProperty("sessionId").GetString()!;

            JsonElement rrJ1 = await p1.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{rrSessionId}}","displayName":"RR1"}""");
            JsonElement rrJ2 = await p2.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{rrSessionId}}","displayName":"RR2"}""");
            JsonElement rrJ3 = await p3.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{rrSessionId}}","displayName":"RR3"}""");

            string rrT1 = rrJ1.GetProperty("participantToken").GetString()!;
            string rrT2 = rrJ2.GetProperty("participantToken").GetString()!;
            string rrT3 = rrJ3.GetProperty("participantToken").GetString()!;

            // In roundRobin, p1 joined first and holds floor. p2 posting fails with NOT_YOUR_TURN
            (JsonDocument notTurn, bool notTurnErr) = await p2.CallAsync(
                "sa_discussion_post", $$"""{"sessionId":"{{rrSessionId}}","participantToken":"{{rrT2}}","content":"premature"}""");
            keep.Add(notTurn);
            Assert.True(notTurnErr);
            Assert.Equal("NOT_YOUR_TURN", McpTestSupport.ErrorCodeOf(notTurn));

            // p1 posts with pass: true -> floor moves to p2
            JsonElement passed = await p1.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{rrSessionId}}","participantToken":"{{rrT1}}","pass":true}""");
            Assert.Equal(rrJ2.GetProperty("handle").GetString(), passed.GetProperty("floor").GetProperty("handle").GetString());

            // p2 leaves session -> floor advances to p3
            await p2.OkAsync(keep, "sa_discussion_leave", $$"""{"sessionId":"{{rrSessionId}}","participantToken":"{{rrT2}}"}""");

            // p3 can now post
            JsonElement p3Posted = await p3.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{rrSessionId}}","participantToken":"{{rrT3}}","content":"p3 speaking"}""");
            Assert.False(p3Posted.GetProperty("duplicate").GetBoolean());
        }
        finally
        {
            foreach (JsonDocument doc in keep)
            {
                doc.Dispose();
            }
        }
    }

    [Fact]
    public async Task MultiProcessLongPollingWaitSecondsAndIdleTimeoutEnforcedAcrossPipe()
    {
        var keep = new List<JsonDocument>();
        try
        {
            string sharedRoot = NewDataRoot();
            ServerProcess p1 = await StartAsync(sharedRoot);
            ServerProcess p2 = await StartAsync(sharedRoot);

            JsonElement created = await p1.OkAsync(keep, "sa_discussion_create", """{"title":"long poll test"}""");
            string sessionId = created.GetProperty("sessionId").GetString()!;

            JsonElement j1 = await p1.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Poller1"}""");
            JsonElement j2 = await p2.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Poller2"}""");
            string t1 = j1.GetProperty("participantToken").GetString()!;
            string t2 = j2.GetProperty("participantToken").GetString()!;
            long seen = j2.GetProperty("lastSequence").GetInt64();

            // 1. Quiet read with waitSeconds = 1 times out with timedOut: true and empty messages
            JsonElement quiet = await p2.OkAsync(keep, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","afterSequence":{{seen}},"waitSeconds":1}""");
            Assert.True(quiet.GetProperty("timedOut").GetBoolean());
            Assert.Empty(quiet.GetProperty("messages").EnumerateArray());

            // 2. Read with waitSeconds = 10 starts; p1 posts after 200ms -> p2 wakes immediately
            Task<(JsonDocument Doc, bool Error)> waitTask = p2.CallAsync(
                "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","afterSequence":{{seen}},"waitSeconds":10}""");

            await Task.Delay(200);
            await p1.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t1}}","content":"wake up!"}""");

            var sw = Stopwatch.StartNew();
            (JsonDocument wakeDoc, bool wakeErr) = await waitTask;
            sw.Stop();
            keep.Add(wakeDoc);

            Assert.False(wakeErr, wakeDoc.RootElement.GetRawText());
            JsonElement wakeRes = McpTestSupport.Structured(wakeDoc);
            Assert.False(wakeRes.GetProperty("timedOut").GetBoolean());
            Assert.Single(wakeRes.GetProperty("messages").EnumerateArray());
            Assert.True(sw.ElapsedMilliseconds < 5000, $"Long poll took {sw.ElapsedMilliseconds}ms to wake up");
        }
        finally
        {
            foreach (JsonDocument doc in keep)
            {
                doc.Dispose();
            }
        }
    }

    [Fact]
    public async Task MultiProcessCapacityAndRateLimitsAreEnforcedUniformlyAcrossHubAndRemote()
    {
        var keep = new List<JsonDocument>();
        try
        {
            string sharedRoot = NewDataRoot();
            WriteDiscussionConfig(sharedRoot, new
            {
                MaxActiveSessions = 1,
                MaxParticipantsPerSession = 2,
                MaxMessageBytes = 256,
                MaxPostsPerMinute = 2
            });

            ServerProcess p1 = await StartAsync(sharedRoot);
            ServerProcess p2 = await StartAsync(sharedRoot);

            // 1. MaxActiveSessions = 1
            JsonElement s1 = await p1.OkAsync(keep, "sa_discussion_create", """{"title":"first session"}""");
            string sessionId = s1.GetProperty("sessionId").GetString()!;

            (JsonDocument s2Doc, bool s2Err) = await p2.CallAsync("sa_discussion_create", """{"title":"second session"}""");
            keep.Add(s2Doc);
            Assert.True(s2Err);
            Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(s2Doc));

            // 2. MaxParticipantsPerSession = 2
            JsonElement j1 = await p1.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Agent1"}""");
            JsonElement j2 = await p2.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Agent2"}""");
            string t2 = j2.GetProperty("participantToken").GetString()!;

            (JsonDocument p3Doc, bool p3Err) = await p2.CallAsync("sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Agent3"}""");
            keep.Add(p3Doc);
            Assert.True(p3Err);
            Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(p3Doc));

            // 3. MaxMessageBytes = 256
            string largeContent = new string('x', 300);
            (JsonDocument largeDoc, bool largeErr) = await p2.CallAsync(
                "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","content":"{{largeContent}}"}""");
            keep.Add(largeDoc);
            Assert.True(largeErr);
            Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(largeDoc));

            // 4. MaxPostsPerMinute = 2
            await p2.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","content":"post 1"}""");
            await p2.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","content":"post 2"}""");

            (JsonDocument rateDoc, bool rateErr) = await p2.CallAsync(
                "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","content":"post 3"}""");
            keep.Add(rateDoc);
            Assert.True(rateErr);
            Assert.Equal("RATE_LIMITED", McpTestSupport.ErrorCodeOf(rateDoc));
        }
        finally
        {
            foreach (JsonDocument doc in keep)
            {
                doc.Dispose();
            }
        }
    }

    [Fact]
    public async Task MultiProcessSharedAnalysisAttachAndGetPreservesBitExactJsonSha256AndCanonicalDeduplication()
    {
        var keep = new List<JsonDocument>();
        try
        {
            string sharedRoot = NewMarketDataRoot("msft");
            ServerProcess p1 = await StartAsync(sharedRoot);
            ServerProcess p2 = await StartAsync(sharedRoot);

            JsonElement created = await p1.OkAsync(keep, "sa_discussion_create", """{"title":"shared analysis session"}""");
            string sessionId = created.GetProperty("sessionId").GetString()!;

            JsonElement j1 = await p1.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Alice"}""");
            JsonElement j2 = await p2.OkAsync(keep, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Bob"}""");
            string t1 = j1.GetProperty("participantToken").GetString()!;
            string t2 = j2.GetProperty("participantToken").GetString()!;

            const string Args1 = """{"symbol":"MSFT","timeFrame":"D1","indicators":[{"id":"r","type":"RSI"}]}""";

            // 1. Process 1 attaches analysis
            JsonElement attached = await p1.OkAsync(
                keep, "sa_discussion_attach_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t1}}","tool":"sa_calculate_indicators","arguments":{{Args1}},"note":"test analysis"}""");
            Assert.Equal(1, attached.GetProperty("contextId").GetInt32());
            Assert.False(attached.GetProperty("reused").GetBoolean());
            string sha256 = attached.GetProperty("sha256").GetString()!;
            Assert.Equal(64, sha256.Length);

            // 2. Process 2 (remote over pipe) fetches analysis; verifies byte-for-byte SHA256 equality
            JsonElement fetched = await p2.OkAsync(
                keep, "sa_discussion_get_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","contextId":1}""");
            Assert.Equal(1, fetched.GetProperty("contextId").GetInt32());
            Assert.Equal(sha256, fetched.GetProperty("sha256").GetString());
            string storedResult = fetched.GetProperty("result").GetRawText();
            string computedSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storedResult))).ToLowerInvariant();
            Assert.Equal(sha256, computedSha256);

            // 3. Process 2 attaches with reordered JSON keys -> reused: true, contextId: 1, same sha256
            const string ArgsReordered = """{"indicators":[{"id":"r","type":"RSI"}],"timeFrame":"D1","symbol":"MSFT"}""";
            JsonElement attachedAgain = await p2.OkAsync(
                keep, "sa_discussion_attach_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","tool":"sa_calculate_indicators","arguments":{{ArgsReordered}},"note":"duplicate"}""");
            Assert.True(attachedAgain.GetProperty("reused").GetBoolean());
            Assert.Equal(1, attachedAgain.GetProperty("contextId").GetInt32());
            Assert.Equal(sha256, attachedAgain.GetProperty("sha256").GetString());

            // 4. Process 2 posts referencing contextIds: [1]
            await p2.OkAsync(keep, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t2}}","content":"attached rsi","contextIds":[1]}""");

            // 5. Process 1 reads messages and verifies contextIds linkage across processes
            JsonElement readMsg = await p1.OkAsync(keep, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{t1}}","afterSequence":0}""");
            var msgList = readMsg.GetProperty("messages").EnumerateArray().ToList();
            Assert.Equal(4, msgList.Count);
            JsonElement postMsg = msgList.Last();
            Assert.Equal("attached rsi", postMsg.GetProperty("content").GetString());
            Assert.Contains(1, postMsg.GetProperty("contextIds").EnumerateArray().Select(e => e.GetInt32()));
        }
        finally
        {
            foreach (JsonDocument doc in keep)
            {
                doc.Dispose();
            }
        }
    }
}
