using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Common;

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

    private async Task<ServerProcess> StartAsync(string dataRoot)
    {
        var server = new ServerProcess(dataRoot);
        _servers.Add(server);
        await server.InitializeAsync();
        return server;
    }

    private sealed class ServerProcess : IDisposable
    {
        private readonly Process _process;
        private int _nextId = 1;

        public ServerProcess(string dataRoot)
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
            startInfo.Environment[PathDiscovery.UiTestEnabledEnvironmentVariable] = "1";
            startInfo.Environment[PathDiscovery.UiTestDataRootEnvironmentVariable] = dataRoot;
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
            int id = _nextId++;
            await SendAsync(McpTestSupport.CallMessage(id, tool, argumentsJson));
            JsonDocument response = await ReadResponseAsync(id);
            return (response, McpTestSupport.IsError(response));
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
}
