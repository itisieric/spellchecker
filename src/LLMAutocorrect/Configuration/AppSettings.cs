using System.Text.Json.Serialization;

namespace LLMAutocorrect.Configuration;

public enum CorrectionMode { Conservative, Normal, Clarity }
public enum AutocompleteMode { Conservative, Normal, Aggressive }
public enum AiProviderKind { Ollama, LMStudio, Gemini, OpenAICompatible }
public enum ProviderAuthentication { None, BearerToken, ApiKeyHeader }

public sealed class AiProviderSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public ProviderAuthentication Authentication { get; set; }
    public string AuthenticationHeader { get; set; } = "Authorization";
    public string AuthenticationPrefix { get; set; } = "Bearer";
    public int ContextLength { get; set; } = 4096;
    public bool KeepModelLoaded { get; set; } = true;

    public AiProviderSettings Clone() => new()
    {
        Endpoint = Endpoint,
        Model = Model,
        Authentication = Authentication,
        AuthenticationHeader = AuthenticationHeader,
        AuthenticationPrefix = AuthenticationPrefix,
        ContextLength = ContextLength,
        KeepModelLoaded = KeepModelLoaded
    };
}

public static class AiProviderDefaults
{
    public static AiProviderSettings Create(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Ollama => new()
        {
            Endpoint = "http://127.0.0.1:11434",
            Model = "qwen3.5:4b",
            Authentication = ProviderAuthentication.None,
            ContextLength = 4096,
            KeepModelLoaded = true
        },
        AiProviderKind.LMStudio => new()
        {
            Endpoint = "http://127.0.0.1:1234/v1",
            Model = "qwen3.5-4b",
            Authentication = ProviderAuthentication.None,
            ContextLength = 4096,
            KeepModelLoaded = true
        },
        AiProviderKind.Gemini => new()
        {
            Endpoint = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-3.5-flash-lite",
            Authentication = ProviderAuthentication.ApiKeyHeader,
            AuthenticationHeader = "x-goog-api-key",
            AuthenticationPrefix = string.Empty,
            ContextLength = 4096,
            KeepModelLoaded = false
        },
        _ => new()
        {
            Endpoint = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini",
            Authentication = ProviderAuthentication.BearerToken,
            AuthenticationHeader = "Authorization",
            AuthenticationPrefix = "Bearer",
            ContextLength = 4096,
            KeepModelLoaded = false
        }
    };
}

public sealed class ApplicationRule
{
    public bool Autocorrect { get; set; } = true;
    public bool Autocomplete { get; set; } = true;
}

public sealed class AppSettings
{
    public int SettingsVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AutocompleteEnabled { get; set; } = true;
    public bool PrivateMode { get; set; }
    public bool StartWithWindows { get; set; }
    public CorrectionMode Mode { get; set; } = CorrectionMode.Conservative;
    public AutocompleteMode AutocompleteMode { get; set; } = AutocompleteMode.Normal;
    public AiProviderKind Provider { get; set; } = AiProviderKind.Ollama;
    public AiProviderSettings OllamaProvider { get; set; } = AiProviderDefaults.Create(AiProviderKind.Ollama);
    public AiProviderSettings LMStudioProvider { get; set; } = AiProviderDefaults.Create(AiProviderKind.LMStudio);
    public AiProviderSettings GeminiProvider { get; set; } = AiProviderDefaults.Create(AiProviderKind.Gemini);
    public AiProviderSettings OpenAICompatibleProvider { get; set; } = AiProviderDefaults.Create(AiProviderKind.OpenAICompatible);
    // Retained only so settings written by versions 1-5 can migrate their Gemini model.
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";
    public int IdleDelayMs { get; set; } = 600;
    public int AutocompleteIdleDelayMs { get; set; } = 800;
    public int MinimumCharacters { get; set; } = 8;
    public int MinimumWords { get; set; } = 2;
    public int MaximumTargetCharacters { get; set; } = 300;
    public int MaximumContextCharacters { get; set; } = 500;
    public int AutocompleteContextCharacters { get; set; } = 1000;
    public int MinimumAutocompleteCharacters { get; set; } = 15;
    public int MinimumAutocompleteWords { get; set; } = 3;
    public int MaximumSuggestionCharacters { get; set; } = 100;
    public int AutocompleteCandidates { get; set; } = 3;
    public int MinimumRequestIntervalMs { get; set; } = 750;
    public int MinimumAutocompleteRequestIntervalMs { get; set; } = 1000;
    public bool TerminalAutocompleteEnabled { get; set; } = true;
    public int TerminalAutocompleteIdleDelayMs { get; set; } = 500;
    public int TerminalAutocompleteCandidates { get; set; } = 10;
    public int TerminalMinimumCharacters { get; set; } = 1;
    public int MinimumTerminalRequestIntervalMs { get; set; } = 1000;
    public bool TerminalUsePowerShellHistory { get; set; } = true;
    public bool TerminalRememberCommands { get; set; } = true;
    public int TerminalMaximumHistoryItems { get; set; } = 500;
    public string TerminalCustomInstructions { get; set; } = string.Empty;
    public List<string> TerminalProcesses { get; set; } =
    [
        "cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe",
        "OpenConsole.exe", "conhost.exe"
    ];
    public double MaximumEditRatio { get; set; } = 0.40;
    public string CustomCorrectionInstructions { get; set; } = string.Empty;
    public bool BrowserAddressBarSpellingOnly { get; set; } = true;
    public bool PersonalMemoryEnabled { get; set; } = true;
    public bool ManualRightClickCorrectionEnabled { get; set; } = true;
    public bool SpellingHistoryEnabled { get; set; } = true;
    public bool ShowUncertainCorrectionNotifications { get; set; } = true;
    public int CorrectionRetryDelayMs { get; set; } = 1200;
    public int CorrectionCommitDelayMs { get; set; } = 100;
    public int InsertionRetryDelayMs { get; set; } = 350;
    public int InsertionRetryAttempts { get; set; } = 2;
    public int ArtificialLatencyMs { get; set; }
    public bool ShowNotifications { get; set; }
    public bool AllowPlaintextDiagnosticLogging { get; set; }
    public List<string> ExcludedProcesses { get; set; } =
    [
        "cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe",
        "devenv.exe", "Code.exe", "idea64.exe", "studio64.exe",
        "1Password.exe", "Bitwarden.exe", "KeePass.exe", "KeePassXC.exe",
        "LogonUI.exe", "CredentialUIBroker.exe", "winlogon.exe"
    ];
    public Dictionary<string, ApplicationRule> ApplicationRules { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public AiProviderSettings ActiveProvider => GetProvider(Provider);

    public AiProviderSettings GetProvider(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Ollama => OllamaProvider,
        AiProviderKind.LMStudio => LMStudioProvider,
        AiProviderKind.Gemini => GeminiProvider,
        _ => OpenAICompatibleProvider
    };

    public void SetProvider(AiProviderKind kind, AiProviderSettings value)
    {
        switch (kind)
        {
            case AiProviderKind.Ollama: OllamaProvider = value; break;
            case AiProviderKind.LMStudio: LMStudioProvider = value; break;
            case AiProviderKind.Gemini: GeminiProvider = value; GeminiModel = value.Model; break;
            default: OpenAICompatibleProvider = value; break;
        }
    }
}
