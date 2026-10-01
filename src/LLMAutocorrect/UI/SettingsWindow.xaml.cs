using System.Diagnostics;
using System.Windows;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Memory;

namespace LLMAutocorrect.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsManager _settings;
    private readonly TechnicalDictionary _dictionary;
    private readonly WritingMemory _memory;
    private readonly SpellingHistory _spellingHistory;
    private readonly CommandHistoryStore _commandHistory;
    private readonly DiagnosticsLogger _logger;
    private readonly StartupManager _startup;

    public SettingsWindow(SettingsManager settings, TechnicalDictionary dictionary, WritingMemory memory,
        SpellingHistory spellingHistory, CommandHistoryStore commandHistory, DiagnosticsLogger logger, StartupManager startup)
    {
        InitializeComponent();
        _settings = settings; _dictionary = dictionary; _memory = memory; _spellingHistory = spellingHistory;
        _commandHistory = commandHistory;
        _logger = logger; _startup = startup;
        CorrectionModeCombo.ItemsSource = Enum.GetValues<CorrectionMode>();
        AutocompleteModeCombo.ItemsSource = Enum.GetValues<AutocompleteMode>();
        LoadValues();
    }

    private void LoadValues()
    {
        var s = _settings.Current;
        EnabledCheck.IsChecked = s.Enabled; AutocompleteCheck.IsChecked = s.AutocompleteEnabled;
        ManualRightClickCheck.IsChecked = s.ManualRightClickCorrectionEnabled;
        StartupCheck.IsChecked = s.StartWithWindows; PrivateModeCheck.IsChecked = s.PrivateMode;
        CorrectionModeCombo.SelectedItem = s.Mode; AutocompleteModeCombo.SelectedItem = s.AutocompleteMode;
        ModelText.Text = s.GeminiModel; IdleDelayText.Text = s.IdleDelayMs.ToString();
        AutocompleteDelayText.Text = s.AutocompleteIdleDelayMs.ToString(); EditRatioText.Text = s.MaximumEditRatio.ToString("0.00");
        AddressBarSpellingOnlyCheck.IsChecked = s.BrowserAddressBarSpellingOnly;
        CustomInstructionsText.Text = s.CustomCorrectionInstructions;
        MemoryEnabledCheck.IsChecked = s.PersonalMemoryEnabled;
        SpellingHistoryEnabledCheck.IsChecked = s.SpellingHistoryEnabled;
        UncertainNotificationCheck.IsChecked = s.ShowUncertainCorrectionNotifications;
        SuccessNotificationCheck.IsChecked = s.ShowNotifications;
        TerminalAutocompleteCheck.IsChecked = s.TerminalAutocompleteEnabled;
        TerminalDelayText.Text = s.TerminalAutocompleteIdleDelayMs.ToString();
        TerminalCandidateText.Text = s.TerminalAutocompleteCandidates.ToString();
        TerminalMinimumCharactersText.Text = s.TerminalMinimumCharacters.ToString();
        TerminalPowerShellHistoryCheck.IsChecked = s.TerminalUsePowerShellHistory;
        TerminalRememberCheck.IsChecked = s.TerminalRememberCommands;
        TerminalProcessesText.Text = string.Join(", ", s.TerminalProcesses);
        TerminalInstructionsText.Text = s.TerminalCustomInstructions;
        RefreshMemoryStatus();
        RefreshTerminalHistoryStatus();
        LatencyText.Text = s.ArtificialLatencyMs.ToString(); ExcludedText.Text = string.Join(", ", s.ExcludedProcesses);
        DictionaryPath.Text = _dictionary.FilePath; LogPath.Text = "Metadata-only log: " + _logger.LogPath;
        ApiKeyStatus.Text = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
            ? "GEMINI_API_KEY is not set. Configure it as a user environment variable, then restart this app."
            : "GEMINI_API_KEY is available from the environment.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.Enabled = EnabledCheck.IsChecked == true; s.AutocompleteEnabled = AutocompleteCheck.IsChecked == true;
        s.ManualRightClickCorrectionEnabled = ManualRightClickCheck.IsChecked == true;
        s.PrivateMode = PrivateModeCheck.IsChecked == true; s.StartWithWindows = StartupCheck.IsChecked == true;
        s.Mode = (CorrectionMode)(CorrectionModeCombo.SelectedItem ?? CorrectionMode.Conservative);
        s.AutocompleteMode = (AutocompleteMode)(AutocompleteModeCombo.SelectedItem ?? AutocompleteMode.Normal);
        s.GeminiModel = string.IsNullOrWhiteSpace(ModelText.Text) ? "gemini-3.5-flash-lite" : ModelText.Text.Trim();
        if (int.TryParse(IdleDelayText.Text, out var idle)) s.IdleDelayMs = Math.Clamp(idle, 200, 5000);
        if (int.TryParse(AutocompleteDelayText.Text, out var autoDelay)) s.AutocompleteIdleDelayMs = Math.Clamp(autoDelay, 300, 5000);
        if (double.TryParse(EditRatioText.Text, out var ratio)) s.MaximumEditRatio = Math.Clamp(ratio, 0.05, 1);
        s.BrowserAddressBarSpellingOnly = AddressBarSpellingOnlyCheck.IsChecked == true;
        s.CustomCorrectionInstructions = CustomInstructionsText.Text.Trim();
        s.PersonalMemoryEnabled = MemoryEnabledCheck.IsChecked == true;
        s.SpellingHistoryEnabled = SpellingHistoryEnabledCheck.IsChecked == true;
        s.ShowUncertainCorrectionNotifications = UncertainNotificationCheck.IsChecked == true;
        s.ShowNotifications = SuccessNotificationCheck.IsChecked == true;
        s.TerminalAutocompleteEnabled = TerminalAutocompleteCheck.IsChecked == true;
        if (int.TryParse(TerminalDelayText.Text, out var terminalDelay))
            s.TerminalAutocompleteIdleDelayMs = Math.Clamp(terminalDelay, 200, 5000);
        if (int.TryParse(TerminalCandidateText.Text, out var terminalCandidates))
            s.TerminalAutocompleteCandidates = Math.Clamp(terminalCandidates, 1, 10);
        if (int.TryParse(TerminalMinimumCharactersText.Text, out var terminalMinimumCharacters))
            s.TerminalMinimumCharacters = Math.Clamp(terminalMinimumCharacters, 1, 20);
        s.TerminalUsePowerShellHistory = TerminalPowerShellHistoryCheck.IsChecked == true;
        s.TerminalRememberCommands = TerminalRememberCheck.IsChecked == true;
        s.TerminalProcesses = TerminalProcessesText.Text.Split([',', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.TerminalCustomInstructions = TerminalInstructionsText.Text.Trim();
        if (int.TryParse(LatencyText.Text, out var latency)) s.ArtificialLatencyMs = Math.Clamp(latency, 0, 5000);
        s.ExcludedProcesses = ExcludedText.Text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _settings.SaveAsync();
        _startup.SetEnabled(s.StartWithWindows);
        Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RefreshMemoryStatus();
        RefreshTerminalHistoryStatus();
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
    private void OpenDictionary_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_dictionary.FilePath) { UseShellExecute = true });
    private async void ClearMemory_Click(object sender, RoutedEventArgs e)
    {
        await _memory.ClearAsync();
        RefreshMemoryStatus();
    }
    private void RefreshMemoryStatus() => MemoryStatus.Text =
        $"Remembered phrase patterns: {_memory.PhraseCount}\nRemembered spelling corrections: {_memory.SpellingCount}\n" +
        $"Ranked spelling words: {_spellingHistory.CorrectWordCount}\nMemory: {_memory.FilePath}\nSpreadsheet: {_spellingHistory.WorkbookPath}";

    private void RefreshTerminalHistoryStatus() => TerminalHistoryStatus.Text =
        $"Remembered commands: {_commandHistory.Count}\nHistory file: {_commandHistory.FilePath}";

    private void OpenSpellingHistory_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_spellingHistory.WorkbookPath) { UseShellExecute = true });

    private async void ClearSpellingHistory_Click(object sender, RoutedEventArgs e)
    {
        await _spellingHistory.ClearAsync();
        RefreshMemoryStatus();
    }

    private async void ClearTerminalHistory_Click(object sender, RoutedEventArgs e)
    {
        await _commandHistory.ClearAsync();
        RefreshTerminalHistoryStatus();
    }
}
