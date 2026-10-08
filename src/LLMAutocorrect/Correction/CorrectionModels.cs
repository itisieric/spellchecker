using LLMAutocorrect.Configuration;

namespace LLMAutocorrect.Correction;

public sealed record CorrectionRequest(
    string ContextBefore,
    string TargetText,
    IReadOnlyList<string> TechnicalTerms,
    IReadOnlyList<string> ProtectedTokens,
    CorrectionMode Mode,
    string CustomInstructions = "",
    bool PreserveCapitalizationAndPunctuation = false,
    IReadOnlyList<string>? LearnedSpelling = null,
    bool StrictReplacementOnly = false);

public sealed record CorrectionResult(
    bool ShouldReplace,
    string Replacement,
    string ChangeType,
    bool Uncertain = false,
    string Reason = "")
{
    public static CorrectionResult NoChange { get; } = new(false, string.Empty, "none");
}

public interface ICorrectionProvider
{
    Task<CorrectionResult> CorrectAsync(CorrectionRequest request, CancellationToken cancellationToken);
}

public sealed record CorrectionHistoryEntry(
    string OriginalText,
    string CorrectedText,
    IntPtr WindowHandle,
    int ProcessId,
    string? FocusedElementId,
    DateTimeOffset Timestamp);
