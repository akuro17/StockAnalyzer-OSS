using System;
using System.Linq;
using System.Text;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The one rule for a name that becomes a file in the attestation folder (the instrument id and the manifest source.path): a plain
/// file name that cannot leave the folder on any operating system, is Unicode NFC (exact matching across operating systems), is a
/// name Windows can create (no reserved character, no trailing space or period) and whose stem is not a Windows device name.
/// </summary>
internal static class AttestedFileNames
{
    // The Windows reserved characters; checked on every OS because Path.GetInvalidFileNameChars is OS-specific and a producer
    // file must mean the same thing wherever it is read (https://learn.microsoft.com/windows/win32/fileio/naming-a-file).
    private static readonly char[] ReservedCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private const char FirstPrintableCharacter = ' ';

    public static bool IsPlain(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(ReservedCharacters) < 0
        && !name.Any(c => c < FirstPrintableCharacter)
        && !name.EndsWith(' ') && !name.EndsWith('.')
        && name is not ("." or "..")
        && name.IsNormalized(NormalizationForm.FormC)
        && !WindowsDeviceFileName.IsReserved(name.Split('.')[0]);
}
