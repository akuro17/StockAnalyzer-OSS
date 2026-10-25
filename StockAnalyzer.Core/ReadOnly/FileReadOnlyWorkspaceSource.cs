using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Reads the workspace file without ever locking out the application: the file is opened with
/// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> because the application saves it with an atomic
/// <c>File.Replace</c>, which fails while a reader holds a handle without <see cref="FileShare.Delete"/>
/// (<see cref="WorkspaceSerializationService.LoadAsync"/> opens with <see cref="FileShare.Read"/>, so it is not used).
/// Parsing uses the serializer's own options (<see cref="WorkspaceSerializationService.Deserialize"/>).
/// Retries transient <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> up to 3 times to tolerate
/// the NTFS file rename window of atomic file replacements.
/// </summary>
public sealed class FileReadOnlyWorkspaceSource : IReadOnlyWorkspaceSource
{
    private const int MaxIoAttempts = 3;
    private static readonly int[] RetryDelaysMs = { 15, 30 };

    private readonly string _workspaceFilePath;
    private readonly WorkspaceSerializationService _serializer;

    public FileReadOnlyWorkspaceSource(string workspaceFilePath, WorkspaceSerializationService? serializer = null)
    {
        _workspaceFilePath = workspaceFilePath ?? throw new ArgumentNullException(nameof(workspaceFilePath));
        _serializer = serializer ?? new WorkspaceSerializationService();
    }

    public async Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_workspaceFilePath))
        {
            return null;
        }

        for (int attempt = 0; attempt < MaxIoAttempts; attempt++)
        {
            try
            {
                using var content = new MemoryStream();
                await using (var stream = new FileStream(
                    _workspaceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
                {
                    await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
                }

                return _serializer.Deserialize(content.GetBuffer().AsSpan(0, (int)content.Length))
                    ?? throw new InvalidDataException("The workspace file holds no workspace.");
            }
            catch (FileNotFoundException)
            {
                return null; // replaced between the existence check and the open
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < MaxIoAttempts - 1)
                {
                    await Task.Delay(RetryDelaysMs[attempt], cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new DataUnavailableException("The workspace file could not be read.");
            }
            catch (Exception)
            {
                // IO errors, invalid JSON and constructor guards of the stored records (for example WatchlistProfile) all mean "not readable now".
                throw new DataUnavailableException("The workspace file could not be read.");
            }
        }

        throw new DataUnavailableException("The workspace file could not be read.");
    }
}
