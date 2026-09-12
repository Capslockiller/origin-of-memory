using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed class TurkishFold
{
    private static readonly Regex TokenPattern = new(@"[^\W_]+", RegexOptions.Compiled);
    private const int MinimumTokenLength = 3;
    private const int PrefixLength = 5;

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
