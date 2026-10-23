using System.Text.Json;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Reads the <c>{id}.json</c> template files of one template folder, read-only: the folder is only listed and its files only opened. A folder
/// that does not exist is an empty listing (nothing is created). Shared by every template type a read-only host serves, so the listing order,
/// the file-name forms and the unreadable-file rule are defined once.
/// </summary>
internal sealed class ReadOnlyTemplateFolder<T> where T : TemplateBase
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _options;

    public ReadOnlyTemplateFolder(string directory, JsonSerializerOptions options)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Every readable template, favourites first and then by name (as the tabs list them), and how many files could not be read.</summary>
    public async Task<(IReadOnlyList<T> Templates, int UnreadableFileCount)> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            return (Array.Empty<T>(), 0);
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "*.json");
        }
        catch (DirectoryNotFoundException)
        {
            return (Array.Empty<T>(), 0);
        }

        var templates = new List<T>();
        int unreadable = 0;
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            T? template = await TryReadAsync(file, cancellationToken).ConfigureAwait(false);
            if (template is null)
            {
                unreadable++;
            }
            else
            {
                templates.Add(template);
            }
        }

        IReadOnlyList<T> ordered = templates
            .OrderByDescending(t => t.IsFavorite)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (ordered, unreadable);
    }

    /// <summary>The template with <paramref name="id"/>, or null when there is none or its file cannot be read.</summary>
    public async Task<T?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        // The two file names TemplateService looks for: the hyphenated id, then the "N" format without hyphens.
        foreach (string name in new[] { $"{id}.json", $"{id:N}.json" })
        {
            string path = Path.Combine(_directory, name);
            if (File.Exists(path))
            {
                return await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<T?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (TemplateFailureRule.IsIsolatable(error))
        {
            // A file that cannot be turned into a template is an unreadable file, whatever the reason (D-13 A): invalid JSON, a locked or
            // inaccessible file, or a value the template type rejects while it is built (for example a null side set by a hand edit).
            // One such file must not end the listing of the others. Cancellation is not swallowed.
            return null;
        }
    }
}
