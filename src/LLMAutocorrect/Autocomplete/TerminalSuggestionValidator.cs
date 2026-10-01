using System.Text.RegularExpressions;

namespace LLMAutocorrect.Autocomplete;

public sealed partial class TerminalSuggestionValidator
{
    [GeneratedRegex(@"(^|\s)(format(?:\.com)?|diskpart\s+.*\bclean\b|shutdown(?:\.exe)?|Stop-Computer|Restart-Computer)\b|\brm\s+-rf\b|\bRemove-Item\b.*-(?:Recurse|Force).*-?(?:Force|Recurse)?|\b(?:iwr|curl|wget)\b.*\|\s*(?:iex|sh|bash)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HighRiskCommand();

    [GeneratedRegex(@"(?i)(password|passwd|passphrase|api[_-]?key|secret|token|authorization)\s*(?:=|:|\s)\s*\S+")]
    private static partial Regex SecretAssignment();

    public bool IsSafeContext(string command) => command.Length <= 500 &&
        !command.Any(ch => ch is '\r' or '\n') && !SecretAssignment().IsMatch(command);

    public bool IsSafeHistoryEntry(string command) => IsSafeContext(command) && !HighRiskCommand().IsMatch(command);

    public string? Normalize(string currentCommand, string candidate, int maximumCharacters = 300)
    {
        var text = candidate.Replace("```", string.Empty, StringComparison.Ordinal).TrimEnd();
        if (text.Length == 0 || text.Length > maximumCharacters || text.Any(ch => ch is '\r' or '\n' or '\0')) return null;
        if (SecretAssignment().IsMatch(text) || HighRiskCommand().IsMatch(text)) return null;

        if (text.StartsWith(currentCommand, StringComparison.OrdinalIgnoreCase))
            text = text[currentCommand.Length..];
        else
        {
            var overlap = Math.Min(currentCommand.Length, text.Length);
            while (overlap > 0 && !currentCommand.EndsWith(text[..overlap], StringComparison.OrdinalIgnoreCase)) overlap--;
            if (overlap > 0) text = text[overlap..];
        }

        if (text.Length == 0 || text.All(char.IsWhiteSpace)) return null;
        return text;
    }
}
