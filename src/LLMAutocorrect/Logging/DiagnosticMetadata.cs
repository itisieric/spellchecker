using LLMAutocorrect.Configuration;
using LLMAutocorrect.Providers;

namespace LLMAutocorrect.Logging;

internal static class DiagnosticMetadata
{
    private static readonly HashSet<string> ProviderStatuses = new(StringComparer.Ordinal)
    {
        "CONFIGURATION_ERROR", "NETWORK_ERROR", "TIMEOUT", "INVALID_RESPONSE", "NO_CANDIDATE",
        "INVALID_ARGUMENT", "UNAUTHENTICATED", "PERMISSION_DENIED", "NOT_FOUND", "RESOURCE_EXHAUSTED",
        "FAILED_PRECONDITION", "INTERNAL", "UNAVAILABLE", "DEADLINE_EXCEEDED"
    };

    public static Dictionary<string, object?> Failure(Exception exception)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Type"] = exception.GetType().Name, ["HResult"] = exception.HResult
        };
        if (exception is ProviderUnavailableException provider)
        {
            fields["HttpStatus"] = provider.StatusCode;
            fields["ProviderStatus"] = provider.ProviderStatus is { } status && ProviderStatuses.Contains(status)
                ? status : "OTHER";
            fields["Retryable"] = provider.IsRetryable;
        }
        return fields;
    }

    public static Dictionary<string, object?> Settings(AppSettings settings)
    {
        var profile = settings.ActiveProvider;
        return new()
        {
            ["SettingsVersion"] = settings.SettingsVersion,
            ["Provider"] = settings.Provider.ToString(),
            ["Model"] = profile.Model,
            ["EndpointScope"] = ProviderEndpointPolicy.TryValidate(profile.Endpoint, out var endpoint, out _)
                ? ProviderEndpointPolicy.IsSameComputer(endpoint) ? "ThisComputer"
                    : ProviderEndpointPolicy.IsPrivateNetwork(endpoint) ? "LAN" : "Remote"
                : "Invalid",
            ["ContextLength"] = profile.ContextLength, ["KeepModelLoaded"] = profile.KeepModelLoaded,
            ["ReleaseLocalModelResources"] = settings.ReleaseLocalModelResources,
            ["Enabled"] = settings.Enabled, ["AutocompleteEnabled"] = settings.AutocompleteEnabled,
            ["TerminalAutocompleteEnabled"] = settings.TerminalAutocompleteEnabled,
            ["PrivateMode"] = settings.PrivateMode, ["CorrectionMode"] = settings.Mode.ToString(),
            ["IdleDelayMs"] = settings.IdleDelayMs, ["MinimumRequestIntervalMs"] = settings.MinimumRequestIntervalMs,
            ["MinimumCharacters"] = settings.MinimumCharacters, ["MinimumWords"] = settings.MinimumWords,
            ["MaximumEditRatio"] = settings.MaximumEditRatio,
            ["CommitDelayMs"] = settings.CorrectionCommitDelayMs,
            ["InsertionRetryAttempts"] = settings.InsertionRetryAttempts,
            ["InsertionRetryDelayMs"] = settings.InsertionRetryDelayMs,
            ["AutocompleteDelayMs"] = settings.AutocompleteIdleDelayMs,
            ["MinimumAutocompleteRequestIntervalMs"] = settings.MinimumAutocompleteRequestIntervalMs,
            ["MinimumAutocompleteCharacters"] = settings.MinimumAutocompleteCharacters,
            ["MinimumAutocompleteWords"] = settings.MinimumAutocompleteWords,
            ["AutocompleteCandidates"] = settings.AutocompleteCandidates,
            ["AutocompleteMode"] = settings.AutocompleteMode.ToString(),
            ["AutocompleteContextCharacters"] = settings.AutocompleteContextCharacters,
            ["TerminalDelayMs"] = settings.TerminalAutocompleteIdleDelayMs,
            ["MinimumTerminalRequestIntervalMs"] = settings.MinimumTerminalRequestIntervalMs,
            ["TerminalMinimumCharacters"] = settings.TerminalMinimumCharacters,
            ["TerminalAutocompleteCandidates"] = settings.TerminalAutocompleteCandidates,
            ["ArtificialLatencyMs"] = settings.ArtificialLatencyMs,
            ["ManualCorrectionEnabled"] = settings.ManualRightClickCorrectionEnabled,
            ["AddressBarSpellingOnly"] = settings.BrowserAddressBarSpellingOnly,
            ["CustomInstructionConfigured"] = !string.IsNullOrWhiteSpace(settings.CustomCorrectionInstructions)
        };
    }
}
