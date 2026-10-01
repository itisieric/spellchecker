using LLMAutocorrect.Configuration;

namespace LLMAutocorrect.Autocomplete;

public sealed record AutocompleteRequest(
    string ContextBefore,
    IReadOnlyList<string> TechnicalTerms,
    int MaxSuggestions,
    int MaxWords,
    AutocompleteMode Mode);

public sealed record AutocompleteCandidate(string Text);
public sealed record AutocompleteResult(IReadOnlyList<AutocompleteCandidate> Candidates)
{
    public static AutocompleteResult Empty { get; } = new(Array.Empty<AutocompleteCandidate>());
}

public interface IAutocompleteProvider
{
    Task<AutocompleteResult> PredictAsync(AutocompleteRequest request, CancellationToken cancellationToken);
}

public sealed class SuggestionState
{
    private readonly object _gate = new();
    private StateData _data = StateData.Empty;

    public StateData Read() { lock (_gate) return _data; }
    public void Set(StateData value) { lock (_gate) _data = value; }
    public void Clear() { lock (_gate) _data = StateData.Empty; }

    public sealed record StateData(
        bool IsVisible, long BufferVersion, int ProcessId, IntPtr WindowHandle,
        string? FocusedElementId, IReadOnlyList<string> Candidates, int SelectedCandidateIndex,
        int AcceptedCharacterCount, bool IsTerminal)
    {
        public static StateData Empty { get; } = new(false, 0, 0, IntPtr.Zero, null, Array.Empty<string>(), 0, 0, false);
        public string CurrentText => Candidates.Count == 0 ? string.Empty : Candidates[SelectedCandidateIndex];
    }
}
