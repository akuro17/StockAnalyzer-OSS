using System.Reflection;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Resources;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Protocol;

/// <summary>What handling one line produced: a response to write now, or a call still running whose response (null when cancelled) arrives later.</summary>
internal readonly record struct DispatchOutcome(string? Immediate, Task<string?>? Deferred = null);

/// <summary>
/// Turns one JSON-RPC line into at most one response line. Handlers receive <see cref="JsonElement"/> and write plain JSON,
/// so the protocol layer stays in this folder and nothing outside it depends on a protocol library. Only tools/call is
/// deferred; everything else is answered immediately and in order.
/// </summary>
internal sealed class McpMessageDispatcher
{
    private const string JsonRpcVersion = "2.0";
    private const string InitializeMethod = "initialize";
    private const string PingMethod = "ping";
    private const string ToolsListMethod = "tools/list";
    private const string ToolsCallMethod = "tools/call";
    private const string ResourcesListMethod = "resources/list";
    private const string ResourcesReadMethod = "resources/read";
    private const string ResourceTemplatesListMethod = "resources/templates/list";
    private const string CancelledNotification = "notifications/cancelled";

    private readonly LogSink _log;
    private readonly McpToolRegistry _registry;
    private readonly ToolExecutor _executor;
    private readonly McpResourceRegistry? _resources;
    private bool _initialized;

    /// <param name="resources">The resources to serve; null serves none (the capability is then not declared and resources/* are unknown methods).</param>
    public McpMessageDispatcher(LogSink log, McpToolRegistry registry, ToolExecutor executor, McpResourceRegistry? resources = null)
    {
        _log = log;
        _registry = registry;
        _executor = executor;
        _resources = resources;
    }

    /// <summary>Handles one line and waits for a deferred call, so the result is the single response line (or null).</summary>
    public async Task<string?> DispatchAsync(string line, CancellationToken cancellationToken)
    {
        DispatchOutcome outcome = Dispatch(line, cancellationToken);
        return outcome.Deferred is null ? outcome.Immediate : await outcome.Deferred.ConfigureAwait(false);
    }

    public DispatchOutcome Dispatch(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new DispatchOutcome(Error(null, JsonRpcErrorCodes.ParseError, "Parse error."));
        }

        using (document)
        {
            return Handle(document.RootElement, cancellationToken);
        }
    }

    /// <summary>The response for a line that cannot be processed at all (for example one longer than the request limit).</summary>
    public static string RejectedLine(string message) => Error(null, JsonRpcErrorCodes.InvalidRequest, message);

    private DispatchOutcome Handle(JsonElement message, CancellationToken cancellationToken)
    {
        // Batches (arrays) are not part of the supported revisions.
        if (message.ValueKind != JsonValueKind.Object)
        {
            return new DispatchOutcome(Error(null, JsonRpcErrorCodes.InvalidRequest, "Invalid request."));
        }

        bool hasId = message.TryGetProperty("id", out JsonElement id);
        bool hasMethod = message.TryGetProperty("method", out JsonElement methodElement);

        if (!hasMethod)
        {
            // A response to a request this server never sent is ignored; anything else is malformed.
            bool isResponse = hasId && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _));
            return new DispatchOutcome(isResponse ? null : Error(null, JsonRpcErrorCodes.InvalidRequest, "Invalid request."));
        }

        if (hasId && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return new DispatchOutcome(Error(null, JsonRpcErrorCodes.InvalidRequest, "Invalid request."));
        }

        if (methodElement.ValueKind != JsonValueKind.String
            || !message.TryGetProperty("jsonrpc", out JsonElement version)
            || version.ValueKind != JsonValueKind.String
            || version.GetString() != JsonRpcVersion)
        {
            return new DispatchOutcome(Error(hasId ? id : null, JsonRpcErrorCodes.InvalidRequest, "Invalid request."));
        }

        string method = methodElement.GetString()!;
        message.TryGetProperty("params", out JsonElement parameters);

        if (!hasId)
        {
            // Notifications never get a response; only cancellation has an effect.
            if (method == CancelledNotification)
            {
                Cancel(parameters);
            }

            return new DispatchOutcome(null);
        }

        try
        {
            if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Params must be an object.");
            }

            // Only the methods this server serves after initialize need the handshake first. Any other method (for example the
            // server/discover probe some clients send before initialize) is simply unknown: -32601 is the signal to fall back to initialize.
            if (!_initialized && (method is ToolsListMethod or ToolsCallMethod || (_resources is not null && method is ResourcesListMethod or ResourcesReadMethod or ResourceTemplatesListMethod)))
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "Server not initialized.");
            }

            if (method == ToolsCallMethod)
            {
                return new DispatchOutcome(null, StartCall(id, parameters, cancellationToken));
            }

            return new DispatchOutcome(Result(id, WriteResult(method, parameters)));
        }
        catch (JsonRpcException error)
        {
            return new DispatchOutcome(Error(id, error.Code, error.Message));
        }
        catch (Exception error)
        {
            _log.Error($"Unhandled error in '{method}': {error}");
            return new DispatchOutcome(Error(id, JsonRpcErrorCodes.InternalError, "Internal error."));
        }
    }

    private void Cancel(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("requestId", out JsonElement requestId)
            && requestId.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            _executor.Cancel(requestId.GetRawText());
        }
    }

    private Action<Utf8JsonWriter> WriteResult(string method, JsonElement parameters)
    {
        return method switch
        {
            InitializeMethod => Initialize(parameters),
            PingMethod => WriteEmptyObject,
            ToolsListMethod => ListTools(parameters),
            ResourcesListMethod when _resources is not null => ListResources(parameters),
            ResourcesReadMethod when _resources is not null => ReadResource(parameters),
            ResourceTemplatesListMethod when _resources is not null => ListResourceTemplates(parameters),
            _ => throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"Method not found: {method}"),
        };
    }

    private static void WriteEmptyObject(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private Action<Utf8JsonWriter> Initialize(JsonElement parameters)
    {
        if (_initialized)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "Server already initialized.");
        }

        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("protocolVersion", out JsonElement requested) || requested.ValueKind != JsonValueKind.String
            || !parameters.TryGetProperty("capabilities", out JsonElement capabilities) || capabilities.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("clientInfo", out JsonElement clientInfo) || clientInfo.ValueKind != JsonValueKind.Object
            || !clientInfo.TryGetProperty("name", out JsonElement clientName) || clientName.ValueKind != JsonValueKind.String)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "initialize requires protocolVersion, capabilities and clientInfo.name.");
        }

        string negotiated = McpProtocolVersions.Negotiate(requested.GetString()!);
        _initialized = true;
        _log.Info($"Initialized for client '{clientName.GetString()}' with protocol {negotiated}.");

        AssemblyName assembly = typeof(McpMessageDispatcher).Assembly.GetName();
        return writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", negotiated);
            writer.WriteStartObject("capabilities");
            writer.WriteStartObject("tools");
            writer.WriteEndObject();
            if (_resources is not null)
            {
                writer.WriteStartObject("resources");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteStartObject("serverInfo");
            writer.WriteString("name", assembly.Name);
            writer.WriteString("version", assembly.Version?.ToString());
            writer.WriteEndObject();
            writer.WriteEndObject();
        };
    }

    private Action<Utf8JsonWriter> ListTools(JsonElement parameters)
    {
        // There is no pagination, so no cursor can be valid.
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cursor", out _))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Invalid cursor.");
        }

        return writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tools");
            foreach (RegisteredTool registered in _registry.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", registered.Tool.Name);
                writer.WriteString("description", registered.Tool.Description);
                writer.WritePropertyName("inputSchema");
                registered.Schema.WriteTo(writer);
                writer.WriteStartObject("annotations");
                // Execution tools start work in the temporary sandbox: not read-only, and a repeated call is not the same call.
                bool execution = registered.Tool.IsExecution;
                writer.WriteBoolean("readOnlyHint", !execution);
                writer.WriteBoolean("destructiveHint", false);
                writer.WriteBoolean("idempotentHint", !execution);
                writer.WriteBoolean("openWorldHint", false);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }

    private static void RejectCursor(JsonElement parameters)
    {
        // There is no pagination, so no cursor can be valid.
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cursor", out _))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Invalid cursor.");
        }
    }

    private Action<Utf8JsonWriter> ListResources(JsonElement parameters)
    {
        RejectCursor(parameters);
        return writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("resources");
            foreach (McpResource resource in _resources!.Resources)
            {
                writer.WriteStartObject();
                writer.WriteString("uri", resource.Uri);
                writer.WriteString("name", resource.Name);
                writer.WriteString("description", resource.Description);
                writer.WriteString("mimeType", resource.MimeType);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }

    /// <summary>The server has no parameterised resources; an empty list answers clients that ask.</summary>
    private Action<Utf8JsonWriter> ListResourceTemplates(JsonElement parameters)
    {
        RejectCursor(parameters);
        return writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("resourceTemplates");
            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }

    private Action<Utf8JsonWriter> ReadResource(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("uri", out JsonElement uri)
            || uri.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(uri.GetString()))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "resources/read requires a uri.");
        }

        if (!_resources!.TryGet(uri.GetString()!, out McpResource? resource))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.ResourceNotFound, "Resource not found.");
        }

        string text = resource!.Read();
        return writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("contents");
            writer.WriteStartObject();
            writer.WriteString("uri", resource.Uri);
            writer.WriteString("mimeType", resource.MimeType);
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }

    private Task<string?> StartCall(JsonElement id, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(name.GetString()))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "tools/call requires a tool name.");
        }

        JsonElement arguments = default;
        if (parameters.TryGetProperty("arguments", out arguments) && arguments.ValueKind != JsonValueKind.Object)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Tool arguments must be an object.");
        }

        if (!_registry.TryGet(name.GetString()!, out RegisteredTool? registered))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Unknown tool: {name.GetString()}");
        }

        // The message document is disposed when this method returns, so the call works on copies.
        JsonElement callId = id.Clone();
        JsonElement callArguments = arguments.ValueKind == JsonValueKind.Object ? arguments.Clone() : EmptyArguments;
        return RunCallAsync(callId, registered, callArguments, cancellationToken);
    }

    private static JsonElement EmptyArguments { get; } = JsonDocument.Parse("{}").RootElement;

    private async Task<string?> RunCallAsync(JsonElement id, RegisteredTool registered, JsonElement arguments, CancellationToken cancellationToken)
    {
        string? callResult = await _executor.ExecuteAsync(id.GetRawText(), registered, arguments, cancellationToken).ConfigureAwait(false);
        return callResult is null ? null : ResultFromJson(id, callResult);
    }

    private static string Result(JsonElement id, Action<Utf8JsonWriter> writeResult)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", JsonRpcVersion);
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            writeResult(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ResultFromJson(JsonElement id, string resultJson)
        => Result(id, writer => writer.WriteRawValue(resultJson));

    private static string Error(JsonElement? id, int code, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", JsonRpcVersion);
            writer.WritePropertyName("id");
            if (id is { } value)
            {
                value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
