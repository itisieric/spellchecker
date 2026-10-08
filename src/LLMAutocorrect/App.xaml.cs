using System.Windows;
using LLMAutocorrect.Autocomplete;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;
using LLMAutocorrect.Input;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Memory;
using LLMAutocorrect.Providers;
using LLMAutocorrect.Security;
using LLMAutocorrect.UI;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private InputRuntime? _runtime;
    private TrayIconManager? _tray;
    private SettingsWindow? _settingsWindow;
    private ProviderClient? _providerClient;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "Local\\LLMAutocorrect.Singleton", out var firstInstance);
        _ownsMutex = firstInstance;
        if (!firstInstance)
        {
            System.Windows.MessageBox.Show("LLM Autocorrect is already running.", "LLM Autocorrect", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var settings = new SettingsManager();
            await settings.LoadAsync();
            var dictionary = new TechnicalDictionary(settings);
            await dictionary.LoadAsync();
            var memory = new WritingMemory(settings);
            await memory.LoadAsync();
            var terminalValidator = new TerminalSuggestionValidator();
            var commandHistory = new CommandHistoryStore(settings, terminalValidator);
            await commandHistory.LoadAsync();
            var spellingHistory = new SpellingHistory(settings);
            await spellingHistory.LoadAsync();
            var logger = new DiagnosticsLogger(settings.DataDirectory);

            var clock = new InputVersionClock();
            var physicalKeys = new PhysicalKeyState();
            var keyboard = new GlobalKeyboardHook(clock, physicalKeys);
            var mouse = new GlobalMouseHook(clock);
            var buffer = new TypingBuffer();
            var foreground = new ForegroundWindowService();
            var focused = new FocusedControlService();
            var sendInput = new SendInputService(physicalKeys);
            var exclusions = new ApplicationExclusionManager(settings);
            var protectedTokens = new ProtectedTokenDetector();
            var correctionValidator = new CorrectionValidator(protectedTokens);
            var history = new CorrectionHistory();
            var statusOverlay = new StatusOverlay();
            var caret = new CaretPositionService();
            var secretStore = new WindowsProviderSecretStore();
            _providerClient = new ProviderClient(settings, secretStore);
            var correctionProvider = new AiCorrectionProvider(_providerClient, settings);
            var autocompleteProvider = new AiAutocompleteProvider(_providerClient, settings);
            var terminalAutocompleteProvider = new AiTerminalAutocompleteProvider(_providerClient, settings);

            var correction = new CorrectionCoordinator(correctionProvider, settings, dictionary, protectedTokens,
                correctionValidator, exclusions, foreground, focused, sendInput, buffer, clock, history, memory,
                spellingHistory, statusOverlay, caret, logger);
            var overlay = new SuggestionOverlay();
            var autocomplete = new AutocompleteCoordinator(autocompleteProvider, terminalAutocompleteProvider,
                settings, dictionary, new SuggestionValidator(), terminalValidator, new SensitiveContentDetector(),
                exclusions, foreground, focused, caret, sendInput, buffer, clock, correction,
                new SuggestionState(), overlay, memory, commandHistory, logger);
            var manualOffer = new ManualCorrectionOverlay();
            var manualCorrection = new ManualCorrectionService(correctionProvider, settings, dictionary, protectedTokens,
                correctionValidator, exclusions, foreground, focused, sendInput, memory, spellingHistory, manualOffer,
                statusOverlay, logger, clock);
            correction.Completed += autocomplete.Schedule;

            _runtime = new InputRuntime(keyboard, mouse, foreground, focused, buffer, clock,
                correction, autocomplete, manualCorrection, settings, logger);
            _runtime.Start();

            var startup = new StartupManager();
            _settingsWindow = new SettingsWindow(settings, dictionary, memory, spellingHistory,
                commandHistory, logger, startup, secretStore, _providerClient);
            _tray = new TrayIconManager(settings, () => _settingsWindow,
                () => _ = Task.Run(() => correction.UndoLatestAsync()),
                manualCorrection.OfferCurrent,
                () => Dispatcher.BeginInvoke(new Action(Shutdown)));
            await logger.WriteAsync("ApplicationStarted", new Dictionary<string, object?>
            {
                ["Provider"] = settings.Current.Provider.ToString(),
                ["Model"] = settings.Current.ActiveProvider.Model,
                ["EndpointIsLocal"] = ProviderEndpointPolicy.TryValidate(settings.Current.ActiveProvider.Endpoint,
                    out var endpoint, out _) && ProviderEndpointPolicy.IsSameComputer(endpoint),
                ["CredentialConfigured"] = secretStore.Contains(settings.Current.Provider) ||
                    (settings.Current.Provider == AiProviderKind.Gemini &&
                     !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
            });
            if (e.Args.Contains("--shutdown-smoke", StringComparer.Ordinal))
            {
                await Task.Delay(500);
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"LLM Autocorrect could not start: {ex.Message}", "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _settingsWindow?.Close();
        if (_runtime is not null) _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _providerClient?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
