using System.Text.RegularExpressions;

namespace LLMAutocorrect.Security;

public sealed partial class ProtectedTokenDetector
{
    [GeneratedRegex(@"https?://[^\s]+|\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b|\b[A-Za-z]:\\[^\s]+|(?<!\w)/(?:[^\s/]+/)*[^\s]+|\b(?:\d{1,3}\.){3}\d{1,3}\b|\b0x[0-9A-Fa-f]+\b|\b\d+(?:\.\d+)?(?:V|A|Hz|kW|ms|Mbps|GB|MB)\b|\b[A-Za-z]+(?:_[A-Za-z0-9]+)+\b|\b[a-z]+(?:[A-Z][A-Za-z0-9]*)+\b|\b[A-Za-z]*\d+[A-Za-z0-9]*\b", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled)]
    private static partial Regex NumberPattern();

    public IReadOnlyList<string> Detect(string text, IEnumerable<string> dictionaryTerms)
    {
        var tokens = TokenPattern().Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var term in dictionaryTerms)
            if (text.Contains(term, StringComparison.Ordinal)) tokens.Add(term);
        return tokens.OrderByDescending(x => x.Length).ToArray();
    }

    public IReadOnlyList<string> DetectNumbers(string text) =>
        NumberPattern().Matches(text).Select(m => m.Value).ToArray();
}
