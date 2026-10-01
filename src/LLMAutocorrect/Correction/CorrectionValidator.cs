using LLMAutocorrect.Security;

namespace LLMAutocorrect.Correction;

public sealed record CorrectionValidation(bool IsValid, double EditRatio, string Reason);

public sealed class CorrectionValidator
{
    private readonly ProtectedTokenDetector _tokens;
    public CorrectionValidator(ProtectedTokenDetector tokens) => _tokens = tokens;

    public CorrectionValidation Validate(CorrectionRequest request, CorrectionResult result, double maximumEditRatio)
    {
        if (!result.ShouldReplace) return new(false, 0, "no-change");
        var output = result.Replacement?.TrimEnd() ?? string.Empty;
        if (output.Length == 0) return new(false, 1, "empty");
        if (output.Contains("```", StringComparison.Ordinal) || output.Contains("Here is", StringComparison.OrdinalIgnoreCase))
            return new(false, 1, "explanation-or-markdown");
        if (HasSuspiciousRun(output) && !HasSuspiciousRun(request.TargetText))
            return new(false, 1, "repeated-character-run");
        if (output.Length > request.TargetText.Length * 1.75 + 20 || output.Length < request.TargetText.Length * 0.45)
            return new(false, 1, "length");
        if (request.ProtectedTokens.Any(token => !output.Contains(token, StringComparison.Ordinal)))
            return new(false, 1, "protected-token");
        if (request.PreserveCapitalizationAndPunctuation && !PreservesAddressBarStyle(request.TargetText, output))
            return new(false, 1, "address-bar-style");
        var inputNumbers = _tokens.DetectNumbers(request.TargetText);
        var outputNumbers = _tokens.DetectNumbers(output);
        if (!inputNumbers.SequenceEqual(outputNumbers, StringComparer.Ordinal))
            return new(false, 1, "number-changed");

        var distance = Levenshtein(request.TargetText, output);
        var ratio = distance / (double)Math.Max(1, Math.Max(request.TargetText.Length, output.Length));
        return ratio <= maximumEditRatio
            ? new(true, ratio, "valid")
            : new(false, ratio, "edit-ratio");
    }

    public static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
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

    private static bool PreservesAddressBarStyle(string input, string output)
    {
        static string Punctuation(string value) => new(value.Where(char.IsPunctuation).ToArray());
        static string Whitespace(string value) => new(value.Where(char.IsWhiteSpace).ToArray());
        if (!string.Equals(Punctuation(input), Punctuation(output), StringComparison.Ordinal) ||
            !string.Equals(Whitespace(input), Whitespace(output), StringComparison.Ordinal)) return false;

        var inputWords = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var outputWords = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (inputWords.Length != outputWords.Length) return false;
        for (var i = 0; i < inputWords.Length; i++)
        {
            var inputLetters = inputWords[i].Where(char.IsLetter).ToArray();
            var outputLetters = outputWords[i].Where(char.IsLetter).ToArray();
            if (inputLetters.Length == 0 || outputLetters.Length == 0) continue;
            if (inputLetters.All(char.IsLower) && outputLetters.Any(char.IsUpper)) return false;
            if (inputLetters.All(char.IsUpper) && outputLetters.Any(char.IsLower)) return false;
            if (char.IsUpper(inputLetters[0]) != char.IsUpper(outputLetters[0])) return false;
        }
        return true;
    }
}
