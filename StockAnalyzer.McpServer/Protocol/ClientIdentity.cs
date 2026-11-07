using System.Text;
using StockAnalyzer.Core.Discussion;

namespace StockAnalyzer.McpServer.Protocol;

/// <summary>
/// The label of the connected client, copied from <c>clientInfo.name</c> of the <c>initialize</c> request. It is a display label for the discussion board's
/// bookkeeping, not an identity: the client chooses it. Set once by the dispatcher; read by tools that report who called.
/// </summary>
internal sealed class ClientIdentity
{
    private volatile string? _label;

    /// <summary>The cleaned label, or null while no client has initialized.</summary>
    public string? Label => _label;

    /// <summary>Stores the label: control characters become spaces, the text is trimmed and cut to <see cref="DiscussionLimits.ClientLabelMaxChars"/> without splitting a surrogate pair.</summary>
    public void Set(string clientName)
    {
        ArgumentNullException.ThrowIfNull(clientName);
        var cleaned = new StringBuilder(clientName.Length);
        foreach (char character in clientName)
        {
            cleaned.Append(char.IsControl(character) ? ' ' : character);
        }

        string text = cleaned.ToString().Trim();
        if (text.Length > DiscussionLimits.ClientLabelMaxChars)
        {
            int length = DiscussionLimits.ClientLabelMaxChars;
            if (char.IsHighSurrogate(text[length - 1]))
            {
                length--;
            }

            text = text[..length].TrimEnd();
        }

        _label = text;
    }
}
