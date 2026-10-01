using System.Text.RegularExpressions;

namespace LLMAutocorrect.Autocomplete;

public sealed partial class SuggestionValidator
{
    [GeneratedRegex(@"(^|\s)(https?://|[A-Za-z]:\\|/dev/|ssh\s|sudo\s|git\s|python\s|pip\s)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CommandLike();
    [GeneratedRegex(@"[{};]|=>|\b(import|class|def|function|public\s+static)\b|\b\w+_\w+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CodeLike();
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled)]
    private static partial Regex Numbers();

    public bool ContextIsEligible(string context) => !CommandLike().IsMatch(context) && !CodeLike().IsMatch(context);

    public string? Normalize(string context, string candidate, int maximumCharacters)
    {
        var text = candidate.Replace("```", string.Empty, StringComparison.Ordinal).Trim();
        if (text.Length == 0 || text.Length > maximumCharacters) return null;
        if (HasSuspiciousRun(text)) return null;
        if (Numbers().IsMatch(text) && !Numbers().Matches(context).Select(x => x.Value).Any(n => text.Contains(n, StringComparison.Ordinal)))
            return null;

        var words = context.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var predicted = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var overlap = 0;
        for (var n = Math.Min(4, Math.Min(words.Length, predicted.Length)); n > 0; n--)
        {
            if (words[^n..].SequenceEqual(predicted[..n], StringComparer.OrdinalIgnoreCase)) { overlap = n; break; }
        }
        if (overlap > 0) text = string.Join(' ', predicted[overlap..]);
        if (text.Length == 0) return null;

        var punctuation = ",.;:!?)]}".Contains(text[0]);
        var contextEndsSpace = context.Length > 0 && char.IsWhiteSpace(context[^1]);
        if (!punctuation && !contextEndsSpace) text = " " + text;
        return text;
    }

    private static bool HasSuspiciousRun(string value)
    {
        var run = 1;
        for (var i = 1; i < value.Length; i++)
        {
            run = value[i] == value[i - 1] ? run + 1 : 1;
            if (run >= 4) return true;
        }
        return false;
    }
}
