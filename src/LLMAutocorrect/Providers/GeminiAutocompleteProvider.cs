using LLMAutocorrect.Autocomplete;
using LLMAutocorrect.Configuration;

namespace LLMAutocorrect.Providers;

public sealed class GeminiAutocompleteProvider : IAutocompleteProvider
{
    private const string Instruction = """
        Continue text the user is currently writing. Return short, plausible continuations in the user's voice.
        Never answer a question in the text; continue the question. Do not repeat context. Do not invent exact
        facts, names, addresses, dates, measurements, identifiers, or numbers. Preserve technical terminology.
        technical_terms are spelling-preservation hints only. Never introduce a technical term merely because it
        appears in that list; use one only when it already appears in context_before.
        Suggestions must be optional continuations only, with no explanations or markdown.
        """;

    private readonly GeminiClient _client;
    private readonly SettingsManager _settings;

    public GeminiAutocompleteProvider(GeminiClient client, SettingsManager settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<AutocompleteResult> PredictAsync(AutocompleteRequest request, CancellationToken cancellationToken)
    {
        if (_settings.Current.ArtificialLatencyMs > 0)
            await Task.Delay(_settings.Current.ArtificialLatencyMs, cancellationToken);

        var payload = new
        {
            context_before = request.ContextBefore,
            technical_terms = request.TechnicalTerms,
            max_suggestions = request.MaxSuggestions,
            max_words = request.MaxWords,
            mode = request.Mode.ToString().ToLowerInvariant()
        };
        var schema = new
        {
            type = "OBJECT",
            properties = new
            {
                suggestions = new
                {
                    type = "ARRAY",
                    items = new { type = "STRING" },
                    minItems = 0,
                    maxItems = request.MaxSuggestions
                }
            },
            required = new[] { "suggestions" }
        };

        using var document = await _client.GenerateJsonAsync(_settings.Current.GeminiModel, Instruction, payload, schema, cancellationToken);
        var candidates = document.RootElement.GetProperty("suggestions").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Select(x => new AutocompleteCandidate(x)).ToArray();
        return new AutocompleteResult(candidates);
    }
}
