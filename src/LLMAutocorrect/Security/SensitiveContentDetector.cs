using System.Text.RegularExpressions;

namespace LLMAutocorrect.Security;

public sealed partial class SensitiveContentDetector
{
    [GeneratedRegex(@"\b(password|passcode|pin|credit\s*card|api\s*key|private\s*key|recovery\s*code|secret)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SensitiveTerms();

    public bool ContainsSensitiveTopic(string text) => SensitiveTerms().IsMatch(text);
}

