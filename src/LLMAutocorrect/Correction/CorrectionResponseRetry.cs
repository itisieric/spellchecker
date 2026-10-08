namespace LLMAutocorrect.Correction;

internal static class CorrectionResponseRetry
{
    internal static bool ShouldRetry(CorrectionValidation validation, bool alreadyAttempted,
        bool operationIsCurrent) =>
        !alreadyAttempted && operationIsCurrent &&
        string.Equals(validation.Reason, "explanation-or-markdown", StringComparison.Ordinal);

    internal static CorrectionRequest CreateStrictRequest(CorrectionRequest request) =>
        request with { StrictReplacementOnly = true };
}
