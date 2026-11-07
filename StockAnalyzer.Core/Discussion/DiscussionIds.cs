using System;
using System.Security.Cryptography;
using System.Text;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// Draws the identifiers of the discussion board from a cryptographic random source (spec 5.1): the hub epoch tag, session ids and participant tokens.
/// A participant token is the only credential: <c>{epoch}.{22 base64url characters of 16 random bytes}</c>; only its SHA-256 is ever stored.
/// </summary>
public sealed class DiscussionIds
{
    private const char TokenSeparator = '.';

    private readonly Action<byte[]> _fill;

    /// <param name="fill">Fills the array with random bytes. Null uses <see cref="RandomNumberGenerator"/>; a test passes a scripted source to reach the collision redraw.</param>
    public DiscussionIds(Action<byte[]>? fill = null)
    {
        _fill = fill ?? (buffer => RandomNumberGenerator.Fill(buffer));
    }

    /// <summary>A new hub epoch: <see cref="DiscussionLimits.EpochTagChars"/> base32 characters.</summary>
    public string NewEpoch() => Base32(DiscussionLimits.EpochTagChars);

    /// <summary>
    /// A new session id (<c>S</c> + 6 base32 characters), redrawn while <paramref name="isTaken"/> says it is in use (live and closed-retained sessions).
    /// </summary>
    /// <exception cref="InvalidOperationException">Every one of <see cref="DiscussionLimits.IdDrawAttempts"/> draws was taken.</exception>
    public string NewSessionId(Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        for (int attempt = 0; attempt < DiscussionLimits.IdDrawAttempts; attempt++)
        {
            string candidate = DiscussionLimits.SessionIdPrefix + Base32(DiscussionLimits.SessionIdRandomChars);
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No free session id was found.");
    }

    /// <summary>A new participant token for the hub with this epoch.</summary>
    public string NewToken(string epoch)
    {
        ArgumentException.ThrowIfNullOrEmpty(epoch);
        byte[] random = new byte[DiscussionLimits.TokenRandomBytes];
        _fill(random);
        return epoch + TokenSeparator + Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// The epoch a token was issued under. False when the text has no epoch part of the right length; the token itself is not validated here
    /// (the hub compares its hash).
    /// </summary>
    public static bool TryGetEpoch(string? token, out string epoch)
    {
        epoch = string.Empty;
        if (token is null)
        {
            return false;
        }

        int separator = token.IndexOf(TokenSeparator, StringComparison.Ordinal);
        if (separator != DiscussionLimits.EpochTagChars)
        {
            return false;
        }

        epoch = token[..separator];
        return true;
    }

    /// <summary>The SHA-256 of the token's UTF-8 bytes: the only form in which the hub keeps a token.</summary>
    public static byte[] HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Constant-time comparison of two token hashes.</summary>
    public static bool HashesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) => CryptographicOperations.FixedTimeEquals(left, right);

    // The alphabet has 32 characters, so taking the low five bits of a random byte is unbiased.
    private string Base32(int length)
    {
        byte[] random = new byte[length];
        _fill(random);
        var text = new StringBuilder(length);
        foreach (byte value in random)
        {
            text.Append(DiscussionLimits.Base32Alphabet[value & 31]);
        }

        return text.ToString();
    }
}
