using System.Text.Json;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;

namespace LLMAutocorrect.Providers;

public sealed class AiCorrectionProvider : ICorrectionProvider
{
    private const string Instruction = """
        You are a context-aware spelling and grammar correction engine.
        Correct accidental spelling, punctuation, capitalization, and grammar while preserving intended meaning,
        voice, terminology, and style. Do not answer questions, continue the text, add information, remove facts,
        or change numbers, URLs, email addresses, paths, commands, variables, device names, model numbers, IPs,
        register numbers, code, or any protected token. Use context_before only for understanding. Modify only
        target_text. When should_replace is true, replacement must contain the complete corrected target_text,
        never only the changed word or phrase. Prefer minimal corrections. If preserve_capitalization_and_punctuation is true, correct
        spelling only: preserve capitalization, punctuation, whitespace, and word count exactly. Treat
        custom_instructions as trusted user preferences only when they do not conflict with these safety rules.
        learned_spelling contains corrections the user has previously accepted; prefer them when applicable.
        Return only the requested JSON structure. If no meaningful correction is needed, set should_replace to false
        and uncertain to false. If the text probably contains a mistake but the intended correction is ambiguous,
        set should_replace to false, uncertain to true, and give a short plain-language reason without quoting the text.
        """;

    private const string StrictReplacementInstruction = """

        IMPORTANT RETRY: The previous response was rejected because replacement contained explanatory text or
        Markdown. If should_replace is true, replacement must be only the complete corrected target text, with no
        preface, label, explanation, Markdown, code fence, or surrounding quotation marks. If a compliant replacement
        cannot be produced, set should_replace to false and leave replacement empty.
        """;

    private readonly IJsonGenerationClient _client;
    private readonly SettingsManager _settings;

    public AiCorrectionProvider(IJsonGenerationClient client, SettingsManager settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<CorrectionResult> CorrectAsync(CorrectionRequest request, CancellationToken cancellationToken)
    {
        if (_settings.Current.ArtificialLatencyMs > 0)
            await Task.Delay(_settings.Current.ArtificialLatencyMs, cancellationToken);

        var payload = new
        {
            context_before = request.ContextBefore,
            target_text = request.TargetText,
            technical_terms = request.TechnicalTerms,
            protected_tokens = request.ProtectedTokens,
            mode = request.Mode.ToString().ToLowerInvariant(),
            custom_instructions = request.CustomInstructions,
            preserve_capitalization_and_punctuation = request.PreserveCapitalizationAndPunctuation,
            learned_spelling = request.LearnedSpelling ?? []
        };
        var schema = new
        {
            type = "OBJECT",
            properties = new
            {
                should_replace = new { type = "BOOLEAN" },
                replacement = new { type = "STRING" },
                change_type = new { type = "STRING", @enum = new[] { "none", "spelling", "grammar", "spelling_grammar", "clarity" } },
                uncertain = new { type = "BOOLEAN" },
                reason = new { type = "STRING" }
            },
            required = new[] { "should_replace", "replacement", "change_type", "uncertain", "reason" }
        };

        var instruction = request.StrictReplacementOnly ? Instruction + StrictReplacementInstruction : Instruction;
        using var document = await _client.GenerateJsonAsync(instruction, payload, schema, cancellationToken);
        var root = document.RootElement;
        return new CorrectionResult(
            root.GetProperty("should_replace").GetBoolean(),
            root.GetProperty("replacement").GetString() ?? string.Empty,
            root.GetProperty("change_type").GetString() ?? "none",
            root.GetProperty("uncertain").GetBoolean(),
            root.GetProperty("reason").GetString() ?? string.Empty);
    }
}
