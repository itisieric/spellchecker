namespace LLMAutocorrect.Configuration;

public enum CorrectionMode { Conservative, Normal, Clarity }
public enum AutocompleteMode { Conservative, Normal, Aggressive }

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
    public string Provider { get; set; } = "Gemini";
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
}
