using System;
using System.Text;

namespace StockAnalyzer.Core.Discussion;

/// <summary>The text rules of the board (spec 5.8). The server never strips, rewrites or truncates user text: it accepts it as it is or refuses it.</summary>
internal static class DiscussionText
{
    /// <summary>True when the text holds a disallowed control character or an unpaired UTF-16 surrogate.</summary>
    /// <param name="allowTabLineBreaks">TAB, LF and CR are allowed (content, topic, note); false for single-line labels (title, display name, request id).</param>
    public static bool IsInvalid(string text, bool allowTabLineBreaks)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (char.IsHighSurrogate(character))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return true;
            }

            if (character <= 0x1F || character == 0x7F)
            {
                bool lineBreakOrTab = character is '\t' or '\n' or '\r';
                if (!(allowTabLineBreaks && lineBreakOrTab))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasVisibleCharacter(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>True when the text holds an unpaired UTF-16 surrogate.</summary>
    public static bool ContainsUnpairedSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (char.IsHighSurrogate(character))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text cut to at most <paramref name="maxChars"/> UTF-16 characters, never in the middle of a surrogate pair. For log records only; user text is never cut.</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        int length = maxChars;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    /// <summary>The characters of a client request id: letters, digits, underscore and hyphen.</summary>
    public static bool IsRequestIdCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '-';
}
