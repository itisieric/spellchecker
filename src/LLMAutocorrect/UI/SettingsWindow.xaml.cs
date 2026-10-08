using System.Diagnostics;
using System.Windows;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Memory;
using LLMAutocorrect.Providers;
using LLMAutocorrect.Security;

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
    private readonly IProviderSecretStore _secretStore;
    private readonly IProviderConnectionTester _providerTester;
    private readonly Dictionary<AiProviderKind, AiProviderSettings> _providerDrafts = new();
    private readonly Dictionary<AiProviderKind, string> _pendingSecrets = new();
    private AiProviderKind? _displayedProvider;
    private bool _loadingProvider;

    public SettingsWindow(SettingsManager settings, TechnicalDictionary dictionary, WritingMemory memory,
        SpellingHistory spellingHistory, CommandHistoryStore commandHistory, DiagnosticsLogger logger,
        StartupManager startup, IProviderSecretStore secretStore, IProviderConnectionTester providerTester)
    {
        InitializeComponent();
        _settings = settings; _dictionary = dictionary; _memory = memory; _spellingHistory = spellingHistory;
        _commandHistory = commandHistory;
        _logger = logger; _startup = startup; _secretStore = secretStore; _providerTester = providerTester;
        CorrectionModeCombo.ItemsSource = Enum.GetValues<CorrectionMode>();
        AutocompleteModeCombo.ItemsSource = Enum.GetValues<AutocompleteMode>();
        ProviderCombo.ItemsSource = Enum.GetValues<AiProviderKind>();
        ProviderAuthCombo.ItemsSource = Enum.GetValues<ProviderAuthentication>();
        LoadValues();
    }

    private void LoadValues()
    {
        var s = _settings.Current;
        EnabledCheck.IsChecked = s.Enabled; AutocompleteCheck.IsChecked = s.AutocompleteEnabled;
        ManualRightClickCheck.IsChecked = s.ManualRightClickCorrectionEnabled;
        StartupCheck.IsChecked = s.StartWithWindows; PrivateModeCheck.IsChecked = s.PrivateMode;
        CorrectionModeCombo.SelectedItem = s.Mode; AutocompleteModeCombo.SelectedItem = s.AutocompleteMode;
        foreach (var kind in Enum.GetValues<AiProviderKind>())
            _providerDrafts[kind] = s.GetProvider(kind).Clone();
        _loadingProvider = true;
        ProviderCombo.SelectedItem = s.Provider;
        _loadingProvider = false;
        ShowProvider(s.Provider);
        IdleDelayText.Text = s.IdleDelayMs.ToString();
        InsertionRetryDelayText.Text = s.InsertionRetryDelayMs.ToString();
        InsertionRetryAttemptsText.Text = s.InsertionRetryAttempts.ToString();
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
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.Enabled = EnabledCheck.IsChecked == true; s.AutocompleteEnabled = AutocompleteCheck.IsChecked == true;
        s.ManualRightClickCorrectionEnabled = ManualRightClickCheck.IsChecked == true;
        s.PrivateMode = PrivateModeCheck.IsChecked == true; s.StartWithWindows = StartupCheck.IsChecked == true;
        s.Mode = (CorrectionMode)(CorrectionModeCombo.SelectedItem ?? CorrectionMode.Conservative);
        s.AutocompleteMode = (AutocompleteMode)(AutocompleteModeCombo.SelectedItem ?? AutocompleteMode.Normal);
        CaptureCurrentProviderDraft();
        if (ProviderCombo.SelectedItem is not AiProviderKind selectedProvider)
        {
            System.Windows.MessageBox.Show("Select an AI provider.", "AI Provider",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ValidateProvider(_providerDrafts[selectedProvider], out var providerError))
        {
            System.Windows.MessageBox.Show(providerError, "AI Provider", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        try
        {
            foreach (var pending in _pendingSecrets)
                _secretStore.Write(pending.Key, pending.Value);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not save credential", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        s.Provider = selectedProvider;
        foreach (var profile in _providerDrafts) s.SetProvider(profile.Key, profile.Value.Clone());
        if (int.TryParse(IdleDelayText.Text, out var idle)) s.IdleDelayMs = Math.Clamp(idle, 200, 5000);
        if (int.TryParse(InsertionRetryDelayText.Text, out var insertionRetryDelay))
            s.InsertionRetryDelayMs = Math.Clamp(insertionRetryDelay, 100, 3000);
        if (int.TryParse(InsertionRetryAttemptsText.Text, out var insertionRetryAttempts))
            s.InsertionRetryAttempts = Math.Clamp(insertionRetryAttempts, 0, 3);
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
        _pendingSecrets.Clear();
        CredentialPassword.Clear();
        RefreshCredentialStatus(selectedProvider);
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

    private void ProviderCombo_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingProvider || ProviderCombo.SelectedItem is not AiProviderKind selected) return;
        CaptureCurrentProviderDraft();
        ShowProvider(selected);
    }

    private void ShowProvider(AiProviderKind kind)
    {
        if (!_providerDrafts.TryGetValue(kind, out var profile))
            _providerDrafts[kind] = profile = AiProviderDefaults.Create(kind);
        _loadingProvider = true;
        _displayedProvider = kind;
        EndpointText.Text = profile.Endpoint;
        ProviderModelText.Text = profile.Model;
        ProviderAuthCombo.SelectedItem = profile.Authentication;
        AuthHeaderText.Text = profile.AuthenticationHeader;
        AuthPrefixText.Text = profile.AuthenticationPrefix;
        ContextLengthText.Text = profile.ContextLength.ToString();
        KeepModelLoadedCheck.IsChecked = profile.KeepModelLoaded;
        CredentialPassword.Password = _pendingSecrets.GetValueOrDefault(kind, string.Empty);
        ProviderTestStatus.Text = string.Empty;
        ProviderDescription.Text = kind switch
        {
            AiProviderKind.Ollama => "Recommended default. Ollama runs in the background and uses its native local API.",
            AiProviderKind.LMStudio => "Uses LM Studio's OpenAI-compatible API. The address normally ends in /v1.",
            AiProviderKind.Gemini => "Google's paid cloud API. A Gemini API key is required.",
            _ => "For paid or local services that implement OpenAI-compatible chat completions. The address should include its API base path, usually /v1."
        };
        _loadingProvider = false;
        RefreshCredentialStatus(kind);
        RefreshProviderWarning();
    }

    private void CaptureCurrentProviderDraft()
    {
        if (_loadingProvider || _displayedProvider is not { } kind) return;
        var profile = _providerDrafts.GetValueOrDefault(kind) ?? AiProviderDefaults.Create(kind);
        profile.Endpoint = EndpointText.Text.Trim();
        profile.Model = ProviderModelText.Text.Trim();
        profile.Authentication = (ProviderAuthentication)(ProviderAuthCombo.SelectedItem ?? ProviderAuthentication.None);
        profile.AuthenticationHeader = AuthHeaderText.Text.Trim();
        profile.AuthenticationPrefix = AuthPrefixText.Text.Trim();
        if (int.TryParse(ContextLengthText.Text, out var contextLength))
            profile.ContextLength = Math.Clamp(contextLength, 512, 32768);
        profile.KeepModelLoaded = KeepModelLoadedCheck.IsChecked == true;
        _providerDrafts[kind] = profile;
        if (!string.IsNullOrWhiteSpace(CredentialPassword.Password))
            _pendingSecrets[kind] = CredentialPassword.Password;
    }

    private static bool ValidateProvider(AiProviderSettings profile, out string? error)
    {
        if (!ProviderEndpointPolicy.TryValidate(profile.Endpoint, out _, out var endpointError))
        {
            error = endpointError;
            return false;
        }
        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            error = "Enter a model name.";
            return false;
        }
        if (profile.Authentication != ProviderAuthentication.None &&
            string.IsNullOrWhiteSpace(profile.AuthenticationHeader))
        {
            error = "Enter the HTTP header used for authentication.";
            return false;
        }
        error = null;
        return true;
    }

    private async void TestProvider_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentProviderDraft();
        if (_displayedProvider is not { } kind)
        {
            ProviderTestStatus.Text = "Select a provider.";
            return;
        }
        if (!ValidateProvider(_providerDrafts[kind], out var error))
        {
            ProviderTestStatus.Text = error;
            return;
        }
        TestProviderButton.IsEnabled = false;
        ProviderTestStatus.Text = "Testing connection...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _providerTester.TestAsync(kind, _providerDrafts[kind],
                _pendingSecrets.GetValueOrDefault(kind), timeout.Token);
            ProviderTestStatus.Text = result.Message;
        }
        catch (OperationCanceledException) { ProviderTestStatus.Text = "The connection test timed out."; }
        catch (Exception ex) { ProviderTestStatus.Text = "Connection test failed: " + ex.Message; }
        finally { TestProviderButton.IsEnabled = true; }
    }

    private void ResetProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_displayedProvider is not { } kind) return;
        _providerDrafts[kind] = AiProviderDefaults.Create(kind);
        ShowProvider(kind);
    }

    private void ClearCredential_Click(object sender, RoutedEventArgs e)
    {
        if (_displayedProvider is not { } kind) return;
        try
        {
            _secretStore.Delete(kind);
            _pendingSecrets.Remove(kind);
            CredentialPassword.Clear();
            RefreshCredentialStatus(kind);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not clear credential", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ProviderField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingProvider) RefreshProviderWarning();
    }

    private void RefreshProviderWarning()
    {
        if (_loadingProvider || !ProviderEndpointPolicy.TryValidate(EndpointText.Text, out var endpoint, out _))
        {
            ProviderWarning.Text = string.Empty;
            return;
        }
        var auth = (ProviderAuthentication)(ProviderAuthCombo.SelectedItem ?? ProviderAuthentication.None);
        var warning = ProviderEndpointPolicy.SecurityWarning(endpoint, auth);
        if (PrivateModeCheck.IsChecked == true && !ProviderEndpointPolicy.IsSameComputer(endpoint))
            warning = "Private Mode will block this provider because it is not running on this computer.";
        ProviderWarning.Text = warning ?? (ProviderEndpointPolicy.IsSameComputer(endpoint)
            ? "This provider is restricted to this computer."
            : "This address sends correction text to another computer. Use a trusted server and network.");
    }

    private void RefreshCredentialStatus(AiProviderKind kind)
    {
        try
        {
            var stored = _secretStore.Contains(kind);
            var environment = kind switch
            {
                AiProviderKind.Gemini => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")),
                AiProviderKind.OpenAICompatible => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
                _ => false
            };
            CredentialStatus.Text = stored ? "A credential is saved in Windows Credential Manager."
                : environment ? "A credential is available from the provider's environment variable."
                : "No credential is saved. None is needed when authentication is set to None.";
        }
        catch (Exception ex) { CredentialStatus.Text = "Credential status unavailable: " + ex.Message; }
    }
}
