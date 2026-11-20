namespace StockAnalyzer.McpServer.Resources;

/// <summary>
/// A read-only document the server offers: a fixed URI, a name for the client's list, and the JSON or Markdown it returns when read. A document that has to read a file is read asynchronously
/// (<paramref name="ReadAsync"/>, cancellable); a document that is already in memory is given as a plain function.
/// </summary>
internal sealed record McpResource(string Uri, string Name, string Description, string MimeType, Func<CancellationToken, Task<string>> ReadAsync)
{
    public McpResource(string uri, string name, string description, string mimeType, Func<string> read)
        : this(uri, name, description, mimeType, _ => Task.FromResult(read()))
    {
    }
}

/// <summary>
/// A parameterised read-only resource template advertised via <c>resources/templates/list</c> and resolved on <c>resources/read</c>.
/// </summary>
internal sealed record McpResourceTemplate(
    string UriTemplate,
    string Name,
    string Description,
    string MimeType,
    Func<string, McpResource?> TryResolve);

/// <summary>The resources of the server, found by URI or template match. Resources are static documents: the catalogues, allow-listed settings, and active tool docs.</summary>
internal sealed class McpResourceRegistry
{
    public const string JsonMimeType = "application/json";
    public const string MarkdownMimeType = "text/markdown";
    public const string UriScheme = "stockanalyzer://";

    private readonly Dictionary<string, McpResource> _byUri;

    public McpResourceRegistry(IEnumerable<McpResource> resources, IEnumerable<McpResourceTemplate>? templates = null)
    {
        Resources = resources.ToList();
        Templates = templates?.ToList() ?? new List<McpResourceTemplate>();
        _byUri = Resources.ToDictionary(resource => resource.Uri, StringComparer.Ordinal);
    }

    public IReadOnlyList<McpResource> Resources { get; }

    public IReadOnlyList<McpResourceTemplate> Templates { get; }

    public bool TryGet(string uri, out McpResource? resource)
    {
        if (_byUri.TryGetValue(uri, out resource))
        {
            return true;
        }

        foreach (McpResourceTemplate template in Templates)
        {
            if (template.TryResolve(uri) is { } resolved)
            {
                resource = resolved;
                return true;
            }
        }

        resource = null;
        return false;
    }
}
