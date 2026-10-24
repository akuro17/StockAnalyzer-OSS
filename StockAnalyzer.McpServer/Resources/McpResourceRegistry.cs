namespace StockAnalyzer.McpServer.Resources;

/// <summary>A read-only document the server offers: a fixed URI, a name for the client's list, and the JSON it returns when read.</summary>
internal sealed record McpResource(string Uri, string Name, string Description, string MimeType, Func<string> Read);

/// <summary>The resources of the server, found by URI. Resources are static documents: the catalogues and the allow-listed settings.</summary>
internal sealed class McpResourceRegistry
{
    public const string JsonMimeType = "application/json";
    public const string UriScheme = "stockanalyzer://";

    private readonly Dictionary<string, McpResource> _byUri;

    public McpResourceRegistry(IEnumerable<McpResource> resources)
    {
        Resources = resources.ToList();
        _byUri = Resources.ToDictionary(resource => resource.Uri, StringComparer.Ordinal);
    }

    public IReadOnlyList<McpResource> Resources { get; }

    public bool TryGet(string uri, out McpResource? resource) => _byUri.TryGetValue(uri, out resource);
}
