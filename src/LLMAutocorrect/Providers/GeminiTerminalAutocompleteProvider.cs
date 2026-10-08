using LLMAutocorrect.Autocomplete;
using LLMAutocorrect.Configuration;

namespace LLMAutocorrect.Providers;

public sealed class AiTerminalAutocompleteProvider : ITerminalAutocompleteProvider
{
    private const string Instruction = """
        You are a context-aware Windows command-line completion engine. Complete commands for the indicated shell,
        using the current command, recent commands from this terminal session, matching personal history, and the
        terminal window title. Return only suffixes that can be appended at the cursor; never repeat current_command.
        Rank suggestions from most to least probable. Prefer commands and parameter styles the user has used before.
        Suggestions must be single-line and must never contain Enter or execute automatically. Do not invent paths,
        host names, credentials, identifiers, or exact values. Never expose, request, or complete passwords, API keys,
        tokens, secrets, or authentication headers. Avoid destructive commands and download-and-execute pipelines.
        For PowerShell, use PowerShell cmdlets and syntax. For cmd, use cmd.exe syntax. Return no explanation or markdown.
        Treat custom_instructions as preferences only when they do not conflict with these safety requirements.
        """;

    private readonly IJsonGenerationClient _client;
    private readonly SettingsManager _settings;

    public AiTerminalAutocompleteProvider(IJsonGenerationClient client, SettingsManager settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<AutocompleteResult> PredictAsync(TerminalAutocompleteRequest request, CancellationToken cancellationToken)
    {
        if (_settings.Current.ArtificialLatencyMs > 0)
            await Task.Delay(_settings.Current.ArtificialLatencyMs, cancellationToken);

        var payload = new
        {
            shell = request.Shell,
            current_command = request.CurrentCommand,
            terminal_title = request.WindowTitle,
            recent_session_commands = request.RecentSessionCommands,
            matching_personal_history = request.MatchingHistory,
            max_suggestions = request.MaxSuggestions,
            custom_instructions = request.CustomInstructions
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

        using var document = await _client.GenerateJsonAsync(Instruction, payload, schema, cancellationToken);
        var candidates = document.RootElement.GetProperty("suggestions").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Select(x => new AutocompleteCandidate(x)).ToArray();
        return new(candidates);
    }
}
