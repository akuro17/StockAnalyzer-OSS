using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Contract of what the bulk import accepts for a single token, per length and per character class: ASCII letters, digits, '-' and '.' (after the import's own
/// normalisation), the vendor-suffixed forms, and nothing else. The rows were first recorded on the pre-consolidation code, then the non-ASCII rows were flipped to
/// refusals by the ASCII-only fix; they must stay green when the format check moves behind a shared function.
/// Non-ASCII inputs are written as unicode escapes only.
/// </summary>
public class TickerImportSymbolFormatCharacterisationTests
{
    private const int ImportBound = TickerSymbolLimits.ImportMaxLength;

    private static async Task<string[]> ImportAsync(string token)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(token));
        var result = await new TickerImportService().ImportTickersAsync(stream);
        return result.ToArray();
    }

    [Theory]
    [InlineData("AAPL", "AAPL")]
    [InlineData("aapl", "AAPL")]
    [InlineData("brk.b", "BRK-B")]   // the dot becomes a hyphen before the check
    [InlineData("BRK-B", "BRK-B")]
    [InlineData("7203", "7203-T")]   // a four-digit code gets the Japanese suffix
    [InlineData("7203-T", "7203-T")]
    [InlineData("A", "A")]
    [InlineData("1", "1")]
    [InlineData("12345", "12345")]
    public async Task PlainSymbol_IsAcceptedInCanonicalForm(string token, string expected)
    {
        Assert.Equal(new[] { expected }, await ImportAsync(token));
    }

    [Theory]
    [InlineData(ImportBound - 1, true)]
    [InlineData(ImportBound, true)]
    [InlineData(ImportBound + 1, false)]
    public async Task PlainSymbol_FollowsTheImportLengthBound(int length, bool accepted)
    {
        string token = new('A', length);

        Assert.Equal(accepted ? new[] { token } : System.Array.Empty<string>(), await ImportAsync(token));
    }

    [Theory]
    [InlineData("A_B")]
    [InlineData("A/B")]
    [InlineData("A\\B")]
    [InlineData("A:B")]
    [InlineData("A%B")]
    [InlineData("A@B")]
    [InlineData("A+B")]
    [InlineData("^GSPC")]
    [InlineData("\uFF21\uFF22")]   // full-width letters
    [InlineData("\u212A")]         // KELVIN SIGN: not upper-cased to ASCII by the import, so the ASCII-only pattern refuses it
    [InlineData("A\u212A")]
    [InlineData("\u0130")]         // LATIN CAPITAL LETTER I WITH DOT ABOVE
    [InlineData("A\u0130")]
    [InlineData("\u0131")]         // LATIN SMALL LETTER DOTLESS I (stays itself under the invariant upper-casing)
    [InlineData("A\u0131")]
    [InlineData("\u017F")]         // LATIN SMALL LETTER LONG S (upper-cases to ASCII S; refused on the raw character)
    [InlineData("A\u017F")]
    [InlineData("\uFF17\uFF12\uFF10\uFF13")]   // full-width digits (the four-digit code pattern is Unicode-aware)
    public async Task OtherCharacters_AreRejected(string token)
    {
        Assert.Empty(await ImportAsync(token));
    }
}
