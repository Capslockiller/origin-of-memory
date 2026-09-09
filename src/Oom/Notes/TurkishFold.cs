using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// Turkish case folding and tokenization (spec 6.4, scars Y-044 and 10.1 #5).
/// Culture aware <c>ToLower</c> is wrong in both directions here: the invariant
/// culture folds the dotted capital I to <c>i</c> and the dotless I to <c>i</c>
/// as well, and the Turkish culture depends on the machine's locale. The table
/// below is the only folding the index and the query are allowed to use, and
/// both go through the same method so a note can never be written with one
/// folding and searched with another.
/// </summary>
public sealed class TurkishFold
{
    private static readonly Regex TokenPattern = new(@"[^\W_]+", RegexOptions.Compiled);
    private const int MinimumTokenLength = 3;
    private const int PrefixLength = 5;

    /// <summary>
    /// Lower-cases text with an explicit table: <c>I</c> becomes the dotless
    /// <c>ı</c>, the dotted <c>İ</c> becomes <c>i</c>, the remaining Turkish
    /// letters keep their diacritics and everything else is folded by the
    /// invariant table (which is safe once the two I forms are out of the way).
    /// </summary>
    public string Fold(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                'I' => 'ı',
                'İ' => 'i',
                'Ş' => 'ş',
                'Ğ' => 'ğ',
                'Ü' => 'ü',
                'Ö' => 'ö',
                'Ç' => 'ç',
                _ => char.ToLowerInvariant(c)
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// Index and query tokens. Words shorter than three characters are dropped
    /// and every token also contributes a five character prefix, so that Turkish
    /// suffixes do not hide a match. The two dotless/dotted i forms collapse onto
    /// a single token here (scar Y-044: <c>İstanbul</c>, <c>ISTANBUL</c> and
    /// <c>ıstanbul</c> must be one token) — that is a deliberate recall trade:
    /// folding alone keeps them apart, tokenization brings them together.
    /// </summary>
    public IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in TokenizeAll(text))
        {
            if (seen.Add(token))
                tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// Every token occurrence in reading order, prefixes included and nothing removed.
    /// <see cref="Tokenize"/> is this list de-duplicated; a ranker that needs a real term
    /// frequency needs the occurrences, because over distinct tokens BM25's saturation term
    /// is the constant <c>(k1+1)/(1+k1)</c> for every match (lane R2, change a).
    /// </summary>
    public IReadOnlyList<string> TokenizeAll(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>();
        foreach (Match match in TokenPattern.Matches(Fold(text)))
        {
            var token = NeutralizeDotlessI(match.Value);
            if (token.Length < MinimumTokenLength)
                continue;

            tokens.Add(token);
            if (token.Length > PrefixLength)
                tokens.Add(token[..PrefixLength]);
        }

        return tokens;
    }

    private static string NeutralizeDotlessI(string token) => token.Replace('ı', 'i');
}
