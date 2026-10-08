using LLMAutocorrect.Autocomplete;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;
using LLMAutocorrect.Input;
using LLMAutocorrect.Models;
using LLMAutocorrect.Memory;
using LLMAutocorrect.Providers;
using LLMAutocorrect.Security;
using LLMAutocorrect.UI;
using LLMAutocorrect.Windows;
using Forms = System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

if (args.Contains("--sendinput-smoke", StringComparer.Ordinal))
    return RunSendInputSmoke();
if (args.Contains("--layout", StringComparer.Ordinal))
{
    Console.WriteLine($"INPUT size={System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>()} keyboardOffset={System.Runtime.InteropServices.Marshal.OffsetOf<NativeMethods.Input>(nameof(NativeMethods.Input.Keyboard))}");
    Console.WriteLine($"KEYBDINPUT size={System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.KeybdInput>()} extraOffset={System.Runtime.InteropServices.Marshal.OffsetOf<NativeMethods.KeybdInput>(nameof(NativeMethods.KeybdInput.ExtraInfo))}");
    Console.WriteLine($"MOUSEINPUT size={System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MouseInput>()}");
    return 0;
}
if (args.Contains("--editor-message-smoke", StringComparer.Ordinal))
    return RunEditorMessageSmoke();
if (args.Contains("--uia-replacement-smoke", StringComparer.Ordinal))
    return RunUiaReplacementSmoke();

var tests = new (string Name, Action Run)[]
{
    ("minimal correction accepted", MinimalCorrectionAccepted),
    ("protected identifier change rejected", ProtectedIdentifierRejected),
    ("number change rejected", NumberChangeRejected),
    ("large rewrite rejected", LargeRewriteRejected),
    ("repeated correction rejected", RepeatedCorrectionRejected),
    ("suggestion spacing normalized", SuggestionSpacing),
    ("duplicate prefix removed", DuplicatePrefix),
    ("invented number rejected", InventedNumberRejected),
    ("repeated suggestion rejected", RepeatedSuggestionRejected),
    ("sensitive topic detected", SensitiveTopic),
    ("browser address bar detected", BrowserAddressBarDetected),
    ("address bar style preserved", AddressBarStylePreserved),
    ("manual correction permits ordinary punctuation", ManualCorrectionPermitsOrdinaryPunctuation),
    ("replacement failures report the actual cause", ReplacementFailuresReportActualCause),
    ("manual correction popup avoids the pointer menu", ManualCorrectionPopupAvoidsPointerMenu),
    ("terminal context identifies shells and command lines", TerminalContextIdentifiesShellsAndLines),
    ("terminal suggestions append safely", TerminalSuggestionsAppendSafely),
    ("terminal history ranks frequent matching commands", TerminalHistoryRanksFrequentMatches),
    ("terminal processes never receive prose autocorrect", TerminalProcessesNeverReceiveAutocorrect),
    ("terminal defaults provide ten suggestions", TerminalDefaultsProvideTenSuggestions),
    ("terminal completion is independent from regular autocorrect", TerminalCompletionIsIndependent),
    ("Ollama Qwen is the default provider", OllamaQwenIsDefaultProvider),
    ("provider endpoint privacy distinguishes PC LAN and cloud", ProviderEndpointPrivacy),
    ("provider client enforces private mode before network access", ProviderClientEnforcesPrivateMode),
    ("provider schema is normalized for local engines", ProviderSchemaIsNormalized),
    ("Ollama correction uses structured local request", OllamaCorrectionUsesStructuredRequest),
    ("OpenAI-compatible provider sends protected bearer token", OpenAiProviderUsesBearerToken),
    ("provider settings migration retains Gemini and selects Ollama", ProviderSettingsMigration),
    ("provider credentials are not serialized with settings", ProviderCredentialsAreNotSerialized),
    ("Gemini billing errors are actionable and not retried", GeminiBillingErrorsAreActionable),
    ("autocomplete dictionary terms require context", DictionaryTermsRequireContext),
    ("personal memory learns without storing prefixes", PersonalMemoryLearnsSafely),
    ("spelling history ranks words and keeps variants", SpellingHistoryRanksVariants),
    ("autocomplete modifiers do not count as edits", AutocompleteModifiersAreNotEdits),
    ("stale buffer replacement rejected", StaleBufferReplacement),
    ("navigation invalidates buffer", NavigationInvalidates)
    ,("input worker survives a bad event", InputWorkerSurvivesBadEvent)
    ,("native replacement message plan", NativeReplacementMessagePlan)
    ,("enter correction preserves newline", EnterCorrectionPreservesNewline)
    ,("correction preserves trailing typing spaces", CorrectionPreservesTrailingSpaces)
    ,("backspace updates known suffix", BackspaceUpdatesKnownSuffix)
    ,("typing after backspace remains correctable", TypingAfterBackspaceRemainsCorrectable)
    ,("modified backspace invalidates suffix", ModifiedBackspaceInvalidatesSuffix)
    ,("enter then backspace cancels stale line correction", EnterThenBackspaceCancelsLineCorrection)
    ,("safe insertion retry succeeds after transient failures", SafeInsertionRetrySucceeds)
    ,("safe insertion retry stops after typing Enter Backspace or cursor movement", SafeInsertionRetryStopsAfterInput)
    ,("safe insertion retry distinguishes ambiguous and exhausted failures", SafeInsertionRetryDistinguishesFailures)
    ,("mutation currentness is rechecked after waiting for key release", MutationCurrentnessIsRecheckedAfterKeyWait)
    ,("unanchored autocomplete retries only before mutation", UnanchoredInsertionRetryPolicy)
    ,("insertion retry defaults are bounded", InsertionRetryDefaultsAreBounded)
    ,("text mismatch diagnostics do not log sentence content", TextMismatchDiagnosticsAreMetadataOnly)
    ,("leading case-only editor normalization is safe", LeadingCaseOnlyEditorNormalization)
    ,("correction response retry is bounded", CorrectionResponseRetryIsBounded)
    ,("stale physical key state is reconciled", StalePhysicalKeyStateIsReconciled)
    ,("simulated replacement is exact", SimulatedReplacementIsExact)
    ,("1000 mixed Enter and Backspace sequences stay stale-safe", MixedEnterBackspaceStress)
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void MinimalCorrectionAccepted()
{
    var (validator, detector) = CorrectionTools();
    var text = "the VFD dose not work";
    var request = new CorrectionRequest("", text, ["VFD"], detector.Detect(text, ["VFD"]), CorrectionMode.Conservative);
    Assert(validator.Validate(request, new(true, "The VFD does not work.", "spelling_grammar"), .4).IsValid);
}

static void ProtectedIdentifierRejected()
{
    var (validator, detector) = CorrectionTools();
    var text = "serverDataScreenGetList dose not work";
    var request = new CorrectionRequest("", text, [], detector.Detect(text, []), CorrectionMode.Conservative);
    Assert(!validator.Validate(request, new(true, "The server data screen does not work.", "clarity"), 1).IsValid);
}

static void NumberChangeRejected()
{
    var (validator, detector) = CorrectionTools();
    var text = "set it to 9600 bod";
    var request = new CorrectionRequest("", text, [], detector.Detect(text, []), CorrectionMode.Conservative);
    Assert(!validator.Validate(request, new(true, "Set it to 19200 baud.", "spelling"), 1).IsValid);
}

static void LargeRewriteRejected()
{
    var (validator, detector) = CorrectionTools();
    var text = "the motor dose not work";
    var request = new CorrectionRequest("", text, [], detector.Detect(text, []), CorrectionMode.Conservative);
    Assert(!validator.Validate(request, new(true, "Communication with the drive has failed completely.", "clarity"), .4).IsValid);
}

static void RepeatedCorrectionRejected()
{
    var (validator, detector) = CorrectionTools();
    var text = "lets test";
    var request = new CorrectionRequest("", text, [], detector.Detect(text, []), CorrectionMode.Conservative);
    Assert(!validator.Validate(request, new(true, "Let's tttt", "spelling"), 1).IsValid);
}

static void SuggestionSpacing()
{
    var value = new SuggestionValidator().Normalize("I installed the VFD", "but it still fails.", 100);
    Equal(" but it still fails.", value);
    Equal(", but it still fails.", new SuggestionValidator().Normalize("I installed the VFD", ", but it still fails.", 100));
}

static void DuplicatePrefix()
{
    var value = new SuggestionValidator().Normalize("I think the problem is the", "the baud rate.", 100);
    Equal(" baud rate.", value);
}

static void InventedNumberRejected() => Assert(new SuggestionValidator().Normalize("The current is", "47.3 amps.", 100) is null);
static void RepeatedSuggestionRejected() => Assert(new SuggestionValidator().Normalize("This is a test", "......................", 100) is null);
static void SensitiveTopic() => Assert(new SensitiveContentDetector().ContainsSensitiveTopic("my API key is"));

static void BrowserAddressBarDetected()
{
    var address = new FocusedControlIdentity("1.2", false, true, "Address and search bar", "address and search bar", "OmniboxViewViews");
    Assert(address.IsBrowserAddressBar("brave.exe"));
    Assert(!address.IsBrowserAddressBar("notepad.exe"));
    var pageField = new FocusedControlIdentity("1.3", false, true, "Search", "search-box", "TextField");
    Assert(!pageField.IsBrowserAddressBar("brave.exe"));
}

static void AddressBarStylePreserved()
{
    var (validator, detector) = CorrectionTools();
    const string text = "now lets missspess";
    var request = new CorrectionRequest("", text, [], detector.Detect(text, []), CorrectionMode.Conservative,
        "", PreserveCapitalizationAndPunctuation: true);
    Assert(validator.Validate(request, new(true, "now lets misspell", "spelling"), .4).IsValid);
    Assert(!validator.Validate(request, new(true, "Now let's misspell.", "spelling_grammar"), .4).IsValid);
}

static void ManualCorrectionPermitsOrdinaryPunctuation()
{
    var settings = new AppSettings { BrowserAddressBarSpellingOnly = true };
    var notepad = new FocusedControlIdentity("1.4", false, true, "Text editor", "Text Editor", "RichEditD2DPT");
    Assert(!ManualCorrectionService.ShouldPreserveAddressBarStyle(settings, notepad, "notepad.exe"));

    var (validator, detector) = CorrectionTools();
    const string text = "havent typped";
    var standardRequest = new CorrectionRequest("", text, [], detector.Detect(text, []), CorrectionMode.Conservative,
        "", PreserveCapitalizationAndPunctuation: false);
    var correction = new CorrectionResult(true, "haven't typed", "spelling_grammar");
    Assert(validator.Validate(standardRequest, correction, .60).IsValid);

    var addressBar = new FocusedControlIdentity("1.5", false, true, "Address and search bar", "address", "OmniboxViewViews");
    Assert(ManualCorrectionService.ShouldPreserveAddressBarStyle(settings, addressBar, "brave.exe"));
    var addressRequest = standardRequest with { PreserveCapitalizationAndPunctuation = true };
    Equal("address-bar-style", validator.Validate(addressRequest, correction, .60).Reason);
    Equal("Not corrected because address-bar spelling mode preserves capitalization and punctuation.",
        ManualCorrectionService.ValidationMessage("address-bar-style"));
}

static void ReplacementFailuresReportActualCause()
{
    var changed = ManualCorrectionService.ReplacementFailureMessage(TextReplacementResult.TextChanged);
    var paste = ManualCorrectionService.ReplacementFailureMessage(TextReplacementResult.PasteFailed);
    var selection = ManualCorrectionService.ReplacementFailureMessage(TextReplacementResult.SelectionUnavailable,
        "stage=selection timeout attempts=3 focusMatch=True");
    Assert(changed.Contains("original location", StringComparison.OrdinalIgnoreCase));
    Assert(paste.Contains("cursor", StringComparison.OrdinalIgnoreCase));
    Assert(selection.Contains("three attempts", StringComparison.OrdinalIgnoreCase));
    Assert(!selection.Contains("did not allow", StringComparison.OrdinalIgnoreCase));
    Assert(!string.Equals(changed, paste, StringComparison.Ordinal));
}

static void ManualCorrectionPopupAvoidsPointerMenu()
{
    var work = new System.Windows.Rect(0, 0, 1920, 1080);
    var popup = new System.Windows.Size(300, 80);
    var center = ManualCorrectionOverlay.CalculatePosition(new System.Windows.Point(900, 500), work, popup, 16);
    Equal(584d, center.X);
    Equal(404d, center.Y);

    var corner = ManualCorrectionOverlay.CalculatePosition(new System.Windows.Point(8, 8), work, popup, 16);
    Equal(24d, corner.X);
    Equal(24d, corner.Y);

    var bottomRight = ManualCorrectionOverlay.CalculatePosition(new System.Windows.Point(1910, 1070), work, popup, 16);
    Equal(1594d, bottomRight.X);
    Equal(974d, bottomRight.Y);
}

static void TerminalContextIdentifiesShellsAndLines()
{
    string[] processes = ["cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe"];
    Assert(TerminalContext.IsTerminalProcess("PWSH.EXE", processes));
    Assert(!TerminalContext.IsTerminalProcess("notepad.exe", processes));
    Equal("powershell", TerminalContext.DetectShell("WindowsTerminal.exe", "Administrator: PowerShell"));
    Equal("cmd", TerminalContext.DetectShell("cmd.exe", "Command Prompt"));
    Equal("Get-Ch", TerminalContext.CurrentLine("Get-Location\nGet-Ch"));
    Equal("Get-ChildItem", TerminalContext.LastCompletedCommand("Get-Location\nGet-ChildItem\n"));
    Equal(2, TerminalContext.RecentSessionCommands("Get-Location\nGet-ChildItem\n", 10).Count);
}

static void TerminalSuggestionsAppendSafely()
{
    var validator = new TerminalSuggestionValidator();
    Equal("ildItem -Force", validator.Normalize("Get-Ch", "Get-ChildItem -Force"));
    Equal("tatus", validator.Normalize("git s", "status"));
    Assert(validator.Normalize("Remove-Item ", "Remove-Item C:\\ -Recurse -Force") is null);
    Assert(!validator.IsSafeContext("curl example.test -H Authorization:secret-value"));
    Assert(!validator.IsSafeHistoryEntry("shutdown /s /t 0"));
}

static void TerminalHistoryRanksFrequentMatches()
{
    var path = Path.Combine(Path.GetTempPath(), $"llm-autocorrect-command-history-{Guid.NewGuid():N}.json");
    try
    {
        var settings = new SettingsManager();
        settings.Current.TerminalUsePowerShellHistory = false;
        settings.Current.TerminalRememberCommands = true;
        var validator = new TerminalSuggestionValidator();
        var history = new CommandHistoryStore(settings, validator, path);
        history.RecordAsync("git status", "cmd").GetAwaiter().GetResult();
        history.RecordAsync("git stash list", "cmd").GetAwaiter().GetResult();
        history.RecordAsync("git status", "cmd").GetAwaiter().GetResult();
        history.RecordAsync("set API_KEY=secret-value", "cmd").GetAwaiter().GetResult();
        var matches = history.GetMatchingCommands("cmd", "git s", 10);
        Equal(2, matches.Count);
        Equal("git status", matches[0]);
        Equal(2, history.Count);
        history.ClearAsync().GetAwaiter().GetResult();
        Assert(!File.Exists(path));
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void TerminalProcessesNeverReceiveAutocorrect()
{
    var settings = new SettingsManager();
    settings.Current.ExcludedProcesses.Clear();
    var exclusions = new ApplicationExclusionManager(settings);
    Assert(!exclusions.IsAutocorrectAllowed("powershell.exe"));
    Assert(!exclusions.IsAutocorrectAllowed("WindowsTerminal.exe"));
    Assert(exclusions.IsAutocorrectAllowed("notepad.exe"));
}

static void TerminalDefaultsProvideTenSuggestions()
{
    var settings = new AppSettings();
    Assert(settings.TerminalAutocompleteEnabled);
    Equal(10, settings.TerminalAutocompleteCandidates);
    Assert(settings.TerminalProcesses.Contains("pwsh.exe", StringComparer.OrdinalIgnoreCase));
}

static void TerminalCompletionIsIndependent()
{
    var settings = new AppSettings
    {
        Enabled = false,
        AutocompleteEnabled = false,
        TerminalAutocompleteEnabled = true,
        TerminalMinimumCharacters = 1
    };
    Assert(AutocompleteCoordinator.FeatureEnabled(settings, true));
    Assert(!AutocompleteCoordinator.FeatureEnabled(settings, false));
    Equal(1, settings.TerminalMinimumCharacters);

    settings.Enabled = true;
    settings.TerminalAutocompleteEnabled = false;
    Assert(!AutocompleteCoordinator.FeatureEnabled(settings, true));
}

static void OllamaQwenIsDefaultProvider()
{
    var settings = new AppSettings();
    Equal(AiProviderKind.Ollama, settings.Provider);
    Equal("qwen3.5:4b", settings.OllamaProvider.Model);
    Equal("http://127.0.0.1:11434", settings.OllamaProvider.Endpoint);
    Equal("gemini-3.5-flash-lite", settings.GeminiProvider.Model);
}

static void ProviderEndpointPrivacy()
{
    Assert(ProviderEndpointPolicy.TryValidate("http://127.0.0.1:11434", out var loopback, out _));
    Assert(ProviderEndpointPolicy.IsSameComputer(loopback));
    Assert(ProviderEndpointPolicy.TryValidate("http://192.168.1.50:11434", out var lan, out _));
    Assert(!ProviderEndpointPolicy.IsSameComputer(lan));
    Assert(ProviderEndpointPolicy.IsPrivateNetwork(lan));
    Assert(ProviderEndpointPolicy.SecurityWarning(lan, ProviderAuthentication.BearerToken) is not null);
    Assert(ProviderEndpointPolicy.TryValidate("https://api.example.com/v1", out var cloud, out _));
    Assert(!ProviderEndpointPolicy.IsPrivateNetwork(cloud));
    Assert(!ProviderEndpointPolicy.TryValidate("http://user:secret@127.0.0.1:11434", out _, out _));

    var settings = new AppSettings { PrivateMode = true };
    Assert(ProviderEndpointPolicy.IsAllowedByPrivateMode(settings));
    settings.OllamaProvider.Endpoint = "http://192.168.1.50:11434";
    Assert(!ProviderEndpointPolicy.IsAllowedByPrivateMode(settings));
}

static void ProviderSchemaIsNormalized()
{
    var normalized = ProviderClient.NormalizeSchema(new
    {
        type = "OBJECT",
        properties = new { suggestions = new { type = "ARRAY", items = new { type = "STRING" } } }
    });
    Equal("object", normalized.GetProperty("type").GetString());
    Equal("array", normalized.GetProperty("properties").GetProperty("suggestions").GetProperty("type").GetString());
    Equal("string", normalized.GetProperty("properties").GetProperty("suggestions").GetProperty("items").GetProperty("type").GetString());
}

static void ProviderClientEnforcesPrivateMode()
{
    var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
    using var http = new HttpClient(handler);
    var settings = new SettingsManager();
    settings.Current.Provider = AiProviderKind.Ollama;
    settings.Current.PrivateMode = true;
    settings.Current.OllamaProvider.Endpoint = "http://192.168.1.50:11434";
    using var client = new ProviderClient(settings, new MemorySecretStore(), http);
    try
    {
        client.GenerateJsonAsync("test", new { value = "private" }, new { type = "OBJECT" },
            CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Private Mode should have blocked the LAN provider.");
    }
    catch (ProviderUnavailableException ex)
    {
        Assert(!ex.IsRetryable);
        Assert(ex.UserMessage.Contains("Private Mode", StringComparison.Ordinal));
    }
    Equal(0, handler.CallCount);
}

static void OllamaCorrectionUsesStructuredRequest()
{
    var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"should_replace\\\":true,\\\"replacement\\\":\\\"I have not typed.\\\",\\\"change_type\\\":\\\"spelling_grammar\\\",\\\"uncertain\\\":false,\\\"reason\\\":\\\"\\\"}\"}}", Encoding.UTF8, "application/json")
    });
    using var http = new HttpClient(handler);
    var settings = new SettingsManager();
    settings.Current.Provider = AiProviderKind.Ollama;
    settings.Current.OllamaProvider = AiProviderDefaults.Create(AiProviderKind.Ollama);
    using var client = new ProviderClient(settings, new MemorySecretStore(), http);
    var provider = new AiCorrectionProvider(client, settings);
    var result = provider.CorrectAsync(new CorrectionRequest("", "i havent typped", [], [],
        CorrectionMode.Conservative), CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.ShouldReplace);
    Equal("I have not typed.", result.Replacement);
    Equal("http://127.0.0.1:11434/api/chat", handler.RequestUri?.AbsoluteUri);
    Assert(handler.RequestBody?.Contains("\"model\":\"qwen3.5:4b\"", StringComparison.Ordinal) == true);
    Assert(handler.RequestBody?.Contains("\"think\":false", StringComparison.Ordinal) == true);
    Assert(handler.RequestBody?.Contains("\"keep_alive\":-1", StringComparison.Ordinal) == true);
    Assert(handler.RequestBody?.Contains("\"format\":{\"type\":\"object\"", StringComparison.Ordinal) == true);
    Assert(handler.Authorization is null);
}

static void OpenAiProviderUsesBearerToken()
{
    var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"suggestions\\\":[\\\" this works\\\"]}\"}}]}", Encoding.UTF8, "application/json")
    });
    using var http = new HttpClient(handler);
    var settings = new SettingsManager();
    settings.Current.Provider = AiProviderKind.OpenAICompatible;
    settings.Current.OpenAICompatibleProvider = new AiProviderSettings
    {
        Endpoint = "https://provider.example/v1",
        Model = "small-model",
        Authentication = ProviderAuthentication.BearerToken,
        AuthenticationHeader = "Authorization",
        AuthenticationPrefix = "Bearer"
    };
    var secrets = new MemorySecretStore();
    secrets.Write(AiProviderKind.OpenAICompatible, "test-token-value");
    using var client = new ProviderClient(settings, secrets, http);
    var provider = new AiAutocompleteProvider(client, settings);
    var result = provider.PredictAsync(new AutocompleteRequest("finish", [], 3, 8,
        AutocompleteMode.Normal), CancellationToken.None).GetAwaiter().GetResult();
    Equal(1, result.Candidates.Count);
    Equal("https://provider.example/v1/chat/completions", handler.RequestUri?.AbsoluteUri);
    Equal("Bearer test-token-value", handler.Authorization);
    Assert(handler.RequestBody?.Contains("\"response_format\":{\"type\":\"json_schema\"", StringComparison.Ordinal) == true);
}

static void ProviderSettingsMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), $"llm-autocorrect-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            "{\"settingsVersion\":5,\"provider\":\"Gemini\",\"geminiModel\":\"gemini-legacy-test\"}");
        var manager = new SettingsManager(directory);
        manager.LoadAsync().GetAwaiter().GetResult();
        Equal(AiProviderKind.Ollama, manager.Current.Provider);
        Equal("qwen3.5:4b", manager.Current.OllamaProvider.Model);
        Equal("gemini-legacy-test", manager.Current.GeminiProvider.Model);
        Equal(SettingsManager.CurrentSettingsVersion, manager.Current.SettingsVersion);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void ProviderCredentialsAreNotSerialized()
{
    var directory = Path.Combine(Path.GetTempPath(), $"llm-autocorrect-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var manager = new SettingsManager(directory);
        var secrets = new MemorySecretStore();
        secrets.Write(AiProviderKind.Gemini, "must-not-appear-in-settings");
        manager.SaveAsync().GetAwaiter().GetResult();
        var serialized = File.ReadAllText(manager.SettingsPath);
        Assert(!serialized.Contains("must-not-appear-in-settings", StringComparison.Ordinal));
        Equal("must-not-appear-in-settings", secrets.Read(AiProviderKind.Gemini));
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void GeminiBillingErrorsAreActionable()
{
    var depleted = GeminiClient.CreateHttpFailure(System.Net.HttpStatusCode.PaymentRequired,
        "RESOURCE_EXHAUSTED", "Your prepayment credits are depleted.");
    Equal(402, depleted.StatusCode);
    Assert(!depleted.IsRetryable);
    Assert(depleted.UserMessage.Contains("credits", StringComparison.OrdinalIgnoreCase));

    var unavailable = GeminiClient.CreateHttpFailure(System.Net.HttpStatusCode.ServiceUnavailable,
        "UNAVAILABLE", "Service unavailable.");
    Assert(unavailable.IsRetryable);
    Assert(unavailable.UserMessage.Contains("temporarily", StringComparison.OrdinalIgnoreCase));
}

static void DictionaryTermsRequireContext()
{
    string[] terms = ["Modbus", "VFD", "Brave"];
    Equal(0, AutocompleteCoordinator.RelevantTechnicalTerms("please help write this message", terms).Count);
    var relevant = AutocompleteCoordinator.RelevantTechnicalTerms("check the modbus settings", terms);
    Equal(1, relevant.Count);
    Equal("Modbus", relevant[0]);
}

static void PersonalMemoryLearnsSafely()
{
    var path = Path.Combine(Path.GetTempPath(), $"llm-autocorrect-memory-{Guid.NewGuid():N}.json");
    try
    {
        var settings = new SettingsManager();
        var memory = new WritingMemory(settings, path);
        memory.RecordAcceptedAsync("can you give me", "the full code").GetAwaiter().GetResult();
        Assert(memory.RecallPhrases("can you give me", 3).Contains("the full code"));
        var serialized = File.ReadAllText(path);
        Assert(!serialized.Contains("can you give me", StringComparison.OrdinalIgnoreCase));

        var reloaded = new WritingMemory(settings, path);
        reloaded.LoadAsync().GetAwaiter().GetResult();
        Assert(reloaded.RecallPhrases("can you give me", 3).Contains("the full code"));
        Assert(reloaded.RecallPhrases("please check something else", 3).Count == 0);

        reloaded.RecordCorrectionAsync("teh adress is wrong", "the address is wrong", []).GetAwaiter().GetResult();
        Assert(reloaded.GetSpellingHints("teh adress").Count == 0);
        reloaded.RecordCorrectionAsync("teh adress is wrong", "the address is wrong", []).GetAwaiter().GetResult();
        var hints = reloaded.GetSpellingHints("teh adress");
        Assert(hints.Contains("teh -> the"));
        Assert(hints.Contains("adress -> address"));
        reloaded.ClearAsync().GetAwaiter().GetResult();
        Assert(!File.Exists(path));
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
}

static void SpellingHistoryRanksVariants()
{
    var directory = Path.Combine(Path.GetTempPath(), $"llm-autocorrect-history-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var store = Path.Combine(directory, "history.json");
    var workbook = Path.Combine(directory, "history.xlsx");
    try
    {
        var settings = new SettingsManager();
        var history = new SpellingHistory(settings, store, workbook);
        history.LoadAsync().GetAwaiter().GetResult();
        history.RecordCorrectionAsync("a mistkae", "a mistake", []).GetAwaiter().GetResult();
        history.RecordCorrectionAsync("another mistkae", "another mistake", []).GetAwaiter().GetResult();
        history.RecordCorrectionAsync("one mistke", "one mistake", []).GetAwaiter().GetResult();
        history.RecordCorrectionAsync("bad adress", "bad address", []).GetAwaiter().GetResult();
        Equal(2, history.CorrectWordCount);
        Assert(File.Exists(workbook));
        using var archive = ZipFile.OpenRead(workbook);
        Assert(archive.GetEntry("xl/worksheets/sheet1.xml") is not null);
        using var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = reader.ReadToEnd();
        Assert(sheet.Contains("mistake", StringComparison.Ordinal));
        Assert(sheet.Contains("mistkae (2); mistke (1)", StringComparison.Ordinal));
        Assert(sheet.Contains("address", StringComparison.Ordinal));
        Assert(sheet.IndexOf("mistake", StringComparison.Ordinal) < sheet.IndexOf("address", StringComparison.Ordinal));
        var saved = File.ReadAllText(store);
        Assert(!saved.Contains("another mistkae", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static void AutocompleteModifiersAreNotEdits()
{
    Assert(GlobalKeyboardHook.IsModifierKey(NativeMethods.VkMenu));
    Assert(GlobalKeyboardHook.IsModifierKey(NativeMethods.VkControl));
    Assert(GlobalKeyboardHook.IsModifierKey(NativeMethods.VkShift));
    Assert(GlobalKeyboardHook.IsModifierKey(NativeMethods.VkLeftMenu));
    Assert(GlobalKeyboardHook.IsModifierKey(NativeMethods.VkRightMenu));
    Assert(GlobalKeyboardHook.IsModifierKey(NativeMethods.VkLeftControl));
    Assert(GlobalKeyboardHook.IsModifierKey(NativeMethods.VkRightControl));
    Assert(!GlobalKeyboardHook.IsModifierKey(NativeMethods.VkDown));
    Assert(!GlobalKeyboardHook.IsModifierKey(NativeMethods.VkRight));
    var now = DateTimeOffset.UtcNow;
    Equal(AutocompleteKeyAction.IgnoreModifier,
        AutocompleteCoordinator.ClassifyKey(new(1, NativeMethods.VkMenu, null, false, true, false, now)));
    Equal(AutocompleteKeyAction.Cycle,
        AutocompleteCoordinator.ClassifyKey(new(1, NativeMethods.VkDown, null, false, true, false, now)));
    Equal(AutocompleteKeyAction.IgnoreModifier,
        AutocompleteCoordinator.ClassifyKey(new(1, NativeMethods.VkControl, null, true, false, false, now)));
    Equal(AutocompleteKeyAction.AcceptNextWord,
        AutocompleteCoordinator.ClassifyKey(new(1, NativeMethods.VkRight, null, true, false, false, now)));
}

static void StaleBufferReplacement()
{
    var buffer = new TypingBuffer();
    var window = new WindowIdentity(new IntPtr(1), 2, "notepad.exe", "note");
    buffer.Handle(new(1, 0x41, 'a', false, false, false, DateTimeOffset.UtcNow), window);
    Assert(!buffer.TryReplaceSuffix(0, "a", "b", 2));
    Assert(buffer.TryReplaceSuffix(1, "a", "b", 2));
    Equal("b", buffer.GetSnapshot().BufferText);
}

static void NavigationInvalidates()
{
    var buffer = new TypingBuffer();
    var window = new WindowIdentity(new IntPtr(1), 2, "notepad.exe", "note");
    buffer.Handle(new(1, 0x41, 'a', false, false, false, DateTimeOffset.UtcNow), window);
    var result = buffer.Handle(new(2, 0x25, null, false, false, false, DateTimeOffset.UtcNow), window);
    Assert(!result.Snapshot.IsSynchronized && result.Snapshot.BufferText.Length == 0);
}

static void InputWorkerSurvivesBadEvent()
{
    var channel = System.Threading.Channels.Channel.CreateUnbounded<int>();
    channel.Writer.TryWrite(1);
    channel.Writer.TryWrite(2);
    channel.Writer.TryWrite(3);
    channel.Writer.TryComplete();
    var processed = new List<int>();
    var failures = new List<int>();
    InputRuntime.ProcessResilientlyAsync(channel.Reader.ReadAllAsync(), value =>
        {
            processed.Add(value);
            return value == 2
                ? Task.FromException(new ArgumentException("simulated provider failure"))
                : Task.CompletedTask;
        },
        (value, _) =>
        {
            failures.Add(value);
            return Task.FromException(new IOException("simulated logging failure"));
        }, CancellationToken.None)
        .GetAwaiter().GetResult();
    Equal("1,2,3", string.Join(',', processed));
    Equal("2", string.Join(',', failures));
}

static void NativeReplacementMessagePlan()
{
    var plan = SendInputService.BuildMessagePlan(3, true);
    Equal(4, plan.Count);
    Assert(plan.Take(3).All(x => x == NativeMethods.WmChar));
    Equal((uint)NativeMethods.WmPaste, plan[3]);
    var deletion = SendInputService.BuildDeletionInputs(3);
    Equal(6, deletion.Length);
    Assert(deletion.All(x => x.Keyboard.VirtualKey == 0x08));
}

static void EnterCorrectionPreservesNewline()
{
    var extracted = CorrectionCoordinator.ExtractTarget("this is s test\n", 300, 500);
    Equal("this is s test", extracted.Target);
    Equal("\n", extracted.Trailing);
    Equal("This is a test.\n", "This is a test." + extracted.Trailing);
}

static void CorrectionPreservesTrailingSpaces()
{
    var extracted = CorrectionCoordinator.ExtractTarget("this needs corection  ", 300, 500);
    Equal("this needs corection", extracted.Target);
    Equal("  ", extracted.Trailing);
    Equal("this needs correction  ", "this needs correction" + extracted.Trailing);

    var withEnter = CorrectionCoordinator.ExtractTarget("havent typped \n", 300, 500);
    Equal("havent typped", withEnter.Target);
    Equal(" \n", withEnter.Trailing);
}

static void BackspaceUpdatesKnownSuffix()
{
    var buffer = NewBufferWithText("this is s test", out var window, out var version);
    var change = buffer.Handle(new(++version, 0x08, null, false, false, false, DateTimeOffset.UtcNow), window);
    Assert(change.Snapshot.IsSynchronized);
    Equal("this is s tes", change.Snapshot.BufferText);
    Assert(!buffer.TryReplaceSuffix(version - 1, "this is s test", "This is a test.", version + 1));
}

static void TypingAfterBackspaceRemainsCorrectable()
{
    var buffer = NewBufferWithText("now lets missspess", out var window, out var version);
    for (var i = 0; i < 5; i++)
        buffer.Handle(new(++version, NativeMethods.VkBack, null, false, false, false, DateTimeOffset.UtcNow), window);
    foreach (var character in "pell")
        buffer.Handle(new(++version, char.ToUpperInvariant(character), character, false, false, false, DateTimeOffset.UtcNow), window);

    var snapshot = buffer.GetSnapshot();
    Assert(snapshot.IsSynchronized);
    Equal("now lets misspell", snapshot.BufferText);
    Assert(buffer.TryReplaceSuffix(snapshot.BufferVersion, "now lets misspell", "Now let's misspell.", ++version));
}

static void ModifiedBackspaceInvalidatesSuffix()
{
    var buffer = NewBufferWithText("known suffix", out var window, out var version);
    var change = buffer.Handle(new(++version, NativeMethods.VkBack, null, true, false, false, DateTimeOffset.UtcNow), window);
    Assert(!change.Snapshot.IsSynchronized);
    Equal(string.Empty, change.Snapshot.BufferText);
}

static void EnterThenBackspaceCancelsLineCorrection()
{
    var buffer = NewBufferWithText("i just hit enter", out var window, out var version);
    var entered = buffer.Handle(new(++version, 0x0D, null, false, false, false, DateTimeOffset.UtcNow), window);
    Assert(entered.ShouldTriggerImmediately);
    Equal("\n", CorrectionCoordinator.ExtractTarget(entered.Snapshot.BufferText, 300, 500).Trailing);
    var erased = buffer.Handle(new(++version, 0x08, null, false, false, false, DateTimeOffset.UtcNow), window);
    Assert(erased.Snapshot.IsSynchronized);
    Equal("i just hit enter", erased.Snapshot.BufferText);
    Assert(!buffer.TryReplaceSuffix(version - 1, "i just hit enter\n", "I just hit Enter.\n", version + 1));
}

static void SafeInsertionRetrySucceeds()
{
    var attempts = 0;
    var outcome = SafeMutationRetry.RunAsync(
            2, 100,
            () => true,
            () =>
            {
                attempts++;
                return new MutationAttempt<bool>(attempts == 3, $"stage=simulated-{attempts}");
            },
            applied => applied,
            (_, _) => true,
            CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(outcome.Succeeded);
    Equal(MutationStopReason.None, outcome.StopReason);
    Assert(!outcome.ShouldInvalidateTrackedText);
    Equal(3, outcome.Attempts);
    Equal(3, attempts);
    Equal(3, outcome.Diagnostics.Count);
}

static void SafeInsertionRetryStopsAfterInput()
{
    foreach (var input in new[] { "typing", "enter", "backspace", "cursor-movement" })
    {
        var clock = new InputVersionClock();
        var expectedVersion = clock.Current;
        var attempts = 0;
        var outcome = SafeMutationRetry.RunAsync(
                3, 100,
                () => clock.Current == expectedVersion,
                () =>
                {
                    attempts++;
                    clock.Increment();
                    return new MutationAttempt<bool>(false, $"stage={input}");
                },
                applied => applied,
                (_, _) => true,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(!outcome.Succeeded);
        Equal(MutationStopReason.CurrentnessLost, outcome.StopReason);
        Assert(!outcome.ShouldInvalidateTrackedText);
        Equal(1, outcome.Attempts);
        Equal(1, attempts);
    }
}

static void SafeInsertionRetryDistinguishesFailures()
{
    var ambiguous = SafeMutationRetry.RunAsync(
            3, 100,
            () => true,
            () => new MutationAttempt<bool>(false, "stage=fallback-paste verification-failed"),
            applied => applied,
            (_, _) => false,
            CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(!ambiguous.Succeeded);
    Equal(MutationStopReason.AmbiguousMutation, ambiguous.StopReason);
    Equal(1, ambiguous.Attempts);
    Assert(ambiguous.ShouldInvalidateTrackedText);

    var exhausted = SafeMutationRetry.RunAsync(
            1, 100,
            () => true,
            () => new MutationAttempt<bool>(false, "stage=selection-timeout"),
            applied => applied,
            (_, _) => true,
            CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(!exhausted.Succeeded);
    Equal(MutationStopReason.AttemptsExhausted, exhausted.StopReason);
    Equal(2, exhausted.Attempts);
    Assert(!exhausted.ShouldInvalidateTrackedText);
}

static void MutationCurrentnessIsRecheckedAfterKeyWait()
{
    var current = true;
    var waits = 0;
    var attempts = 0;
    var outcome = SafeMutationRetry.RunAsync(
            0, 100,
            () => SafeMutationRetry.CanAttempt(
                () => current,
                () =>
                {
                    waits++;
                    current = false;
                    return true;
                }),
            () =>
            {
                attempts++;
                return new MutationAttempt<bool>(true, "stage=should-not-run");
            },
            applied => applied,
            (_, _) => true,
            CancellationToken.None)
        .GetAwaiter().GetResult();

    Assert(!outcome.Succeeded);
    Equal(MutationStopReason.CurrentnessLost, outcome.StopReason);
    Equal(1, waits);
    Equal(0, attempts);
    Equal(0, outcome.Attempts);
    Assert(!outcome.ShouldInvalidateTrackedText);
}

static void UnanchoredInsertionRetryPolicy()
{
    Assert(SendInputService.CanSafelyRetry(string.Empty, "stage=selection timeout attempts=3"));
    Assert(SendInputService.CanSafelyRetry(string.Empty, "stage=value-pattern unavailable-or-readonly"));
    Assert(!SendInputService.CanSafelyRetry(string.Empty, "stage=fallback-paste verification-failed"));
    Assert(!SendInputService.CanSafelyRetry(string.Empty, "stage=exception type=COMException"));
    Assert(!SendInputService.CanSafelyRetry("original text", "stage=fallback-paste verification-failed"));
    Assert(SendInputService.CanSafelyRetry("original text",
        "stage=fallback-paste verification-failed original-selection-confirmed=True"));
}

static void InsertionRetryDefaultsAreBounded()
{
    var settings = new AppSettings();
    Equal(350, settings.InsertionRetryDelayMs);
    Equal(2, settings.InsertionRetryAttempts);
    Assert(settings.InsertionRetryDelayMs is >= 100 and <= 3000);
    Assert(settings.InsertionRetryAttempts is >= 0 and <= 3);
}

static void TextMismatchDiagnosticsAreMetadataOnly()
{
    var diagnostic = SendInputService.DescribeMismatch("alpha beta", "alpha\u00A0beta");
    Assert(diagnostic.Contains("mismatchIndex=5", StringComparison.Ordinal));
    Assert(diagnostic.Contains("expectedKind=space", StringComparison.Ordinal));
    Assert(diagnostic.Contains("actualKind=nonbreaking-space", StringComparison.Ordinal));
    Assert(!diagnostic.Contains("alpha", StringComparison.Ordinal));
    Assert(!diagnostic.Contains("beta", StringComparison.Ordinal));
}

static void LeadingCaseOnlyEditorNormalization()
{
    Assert(SendInputService.IsLeadingCaseOnlyDifference("this is a test", "This is a test"));
    Assert(SendInputService.IsLeadingCaseOnlyDifference("This is a test", "this is a test"));
    Assert(!SendInputService.IsLeadingCaseOnlyDifference("this is a test", "Xhis is a test"));
    Assert(!SendInputService.IsLeadingCaseOnlyDifference("this is a test", "This is a Test"));
    Assert(!SendInputService.IsLeadingCaseOnlyDifference("1 test", "2 test"));
}

static void CorrectionResponseRetryIsBounded()
{
    var valid = new CorrectionValidation(true, .1, "valid");
    var explanation = new CorrectionValidation(false, 1, "explanation-or-markdown");
    Assert(CorrectionResponseRetry.ShouldRetry(explanation, false, true));
    Assert(!CorrectionResponseRetry.ShouldRetry(explanation, true, true));
    Assert(!CorrectionResponseRetry.ShouldRetry(explanation, false, false));
    Assert(!CorrectionResponseRetry.ShouldRetry(valid, false, true));

    var request = new CorrectionRequest("", "havent typped", [], [], CorrectionMode.Conservative);
    Assert(!request.StrictReplacementOnly);
    Assert(CorrectionResponseRetry.CreateStrictRequest(request).StrictReplacementOnly);
}

static void StalePhysicalKeyStateIsReconciled()
{
    var physicallyDown = new HashSet<int>();
    var state = new PhysicalKeyState(physicallyDown.Contains, TimeSpan.Zero);
    state.KeyDown(NativeMethods.VkShift);
    Assert(state.AnyPressed);
    Assert(state.WaitUntilReleased(TimeSpan.Zero));
    Assert(!state.AnyPressed);
    Equal("pressedKeyCount=0", state.LastWaitDiagnostic);

    physicallyDown.Add(NativeMethods.VkControl);
    state.KeyDown(NativeMethods.VkControl);
    Assert(!state.WaitUntilReleased(TimeSpan.Zero));
    Assert(state.LastWaitDiagnostic.Contains("pressedKeyCount=1", StringComparison.Ordinal));
    physicallyDown.Clear();
    Assert(state.WaitUntilReleased(TimeSpan.Zero));
}

static void SimulatedReplacementIsExact()
{
    const string input = "this is a new test";
    const string replacement = "This is a new test.";
    var simulatedEditor = input;
    simulatedEditor = simulatedEditor[..^input.Length] + replacement;
    Equal(replacement, simulatedEditor);
}

static TypingBuffer NewBufferWithText(string text, out WindowIdentity window, out long version)
{
    var buffer = new TypingBuffer();
    window = new WindowIdentity(new IntPtr(1), 2, "notepad.exe", "note");
    version = 0;
    foreach (var character in text)
        buffer.Handle(new(++version, char.ToUpperInvariant(character), character, false, false, false, DateTimeOffset.UtcNow), window);
    return buffer;
}

static void MixedEnterBackspaceStress()
{
    var random = new Random(29092026);
    for (var iteration = 0; iteration < 1000; iteration++)
    {
        var wordCount = random.Next(2, 9);
        var text = string.Join(' ', Enumerable.Range(0, wordCount).Select(i => $"word{i}"));
        var buffer = NewBufferWithText(text, out var window, out var version);
        var pending = buffer.GetSnapshot();

        if ((iteration & 1) == 0)
        {
            var entered = buffer.Handle(new(++version, 0x0D, null, false, false, false, DateTimeOffset.UtcNow), window);
            var target = CorrectionCoordinator.ExtractTarget(entered.Snapshot.BufferText, 300, 500);
            Equal("\n", target.Trailing);
            Equal(text, target.Target);
        }

        var erased = buffer.Handle(new(++version, 0x08, null, false, false, false, DateTimeOffset.UtcNow), window);
        Assert(erased.Snapshot.IsSynchronized);
        Assert(!buffer.TryReplaceSuffix(pending.BufferVersion, text, "stale replacement", version + 1));

        var next = (char)('a' + random.Next(0, 26));
        var resumed = buffer.Handle(new(++version, char.ToUpperInvariant(next), next, false, false, false, DateTimeOffset.UtcNow), window);
        Assert(resumed.Snapshot.IsSynchronized);
        Equal(((iteration & 1) == 0 ? text : text[..^1]) + next, resumed.Snapshot.BufferText);
    }
}

static (CorrectionValidator, ProtectedTokenDetector) CorrectionTools()
{
    var detector = new ProtectedTokenDetector();
    return (new CorrectionValidator(detector), detector);
}

static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("assertion failed");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
}

static int RunSendInputSmoke()
{
    const string original = "This is a new not pad";
    const string corrected = "This is a new notepad.";
    var expected = corrected + "\n";
    string? actual = null;
    Exception? failure = null;
    using var completed = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new Forms.Form
            {
                Text = "LLMAutocorrect SendInput Test",
                Width = 520,
                Height = 120,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = Forms.FormStartPosition.CenterScreen
            };
            using var textBox = new Forms.TextBox
            {
                Dock = Forms.DockStyle.Fill,
                Text = original,
                Font = new System.Drawing.Font("Segoe UI", 14),
                Multiline = true,
                AcceptsReturn = true
            };
            form.Controls.Add(textBox);
            form.Shown += async (_, _) =>
            {
                try
                {
                    form.Activate();
                    textBox.Focus();
                    textBox.SelectionStart = textBox.TextLength;
                    var oldForeground = TestNative.GetForegroundWindow();
                    var foregroundThread = TestNative.GetWindowThreadProcessId(oldForeground, out _);
                    var currentThread = TestNative.GetCurrentThreadId();
                    var attached = foregroundThread != currentThread && TestNative.AttachThreadInput(currentThread, foregroundThread, true);
                    TestNative.KeybdEvent(0x12, 0, 0, UIntPtr.Zero);
                    TestNative.KeybdEvent(0x12, 0, 0x0002, UIntPtr.Zero);
                    TestNative.ShowWindow(form.Handle, 5);
                    TestNative.BringWindowToTop(form.Handle);
                    TestNative.SetForegroundWindow(form.Handle);
                    TestNative.SetFocus(textBox.Handle);
                    if (attached) TestNative.AttachThreadInput(currentThread, foregroundThread, false);
                    form.Activate();
                    textBox.Focus();
                    TestNative.SetFocus(textBox.Handle);
                    await Task.Delay(250);
                    if (TestNative.GetForegroundWindow() != form.Handle)
                        throw new InvalidOperationException("Test window could not acquire foreground focus.");
                    if (!textBox.ContainsFocus)
                        throw new InvalidOperationException("Test text box could not acquire keyboard focus.");

                    // Exercise the actual Windows Enter and Backspace key paths before
                    // correcting the line. This catches the stale-buffer/caret failures
                    // that pure string simulations cannot reproduce.
                    if (!SendInputService.SendVirtualKeyPair(NativeMethods.VkReturn))
                        throw new InvalidOperationException("Could not send Enter.");
                    await Task.Delay(100);
                    Equal(original + "\n", NormalizeForSmoke(textBox.Text));
                    if (!SendInputService.SendVirtualKeyPair(NativeMethods.VkBack))
                        throw new InvalidOperationException("Could not send Backspace.");
                    await Task.Delay(100);
                    Equal(original, NormalizeForSmoke(textBox.Text));
                    if (!SendInputService.SendVirtualKeyPair(NativeMethods.VkReturn))
                        throw new InvalidOperationException("Could not send the final Enter.");
                    await Task.Delay(100);

                    if (!new SendInputService().ReplacePreviousText(original + "\n", corrected + "\n"))
                        throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "SendInput reported failure.");
                    await Task.Delay(500);
                    actual = NormalizeForSmoke(textBox.Text);
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Forms.Application.Run(form);
        }
        catch (Exception ex) { failure = ex; }
        finally { completed.Set(); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!completed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("SendInput smoke test timed out.");
    thread.Join();
    if (failure is not null) throw failure;
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"SENDINPUT_FAIL expected={ToCodePoints(expected)} actual={ToCodePoints(actual ?? string.Empty)}");
        return 1;
    }
    Console.WriteLine($"SENDINPUT_PASS physical Enter, Backspace, Enter, and exact replacement value={ToCodePoints(actual ?? string.Empty)}");
    return 0;
}

static string ToCodePoints(string value) => string.Join(' ', value.Select(c => $"U+{(int)c:X4}"));
static string NormalizeForSmoke(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

static int RunEditorMessageSmoke()
{
    Exception? failure = null;
    using var completed = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
        Forms.IDataObject? previous = null;
        try
        {
            previous = Forms.Clipboard.GetDataObject();
            RunCase("this is s test", "This is a test.", false);
            RunCase("i just hit enter", "I just hit Enter.", true);

            using var backspaceBox = new Forms.TextBox { Text = "abc" };
            _ = backspaceBox.Handle;
            backspaceBox.SelectionStart = backspaceBox.TextLength;
            Assert(SendInputService.SendBackspaces(backspaceBox.Handle, 1));
            Equal("ab", backspaceBox.Text);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                if (previous is null) Forms.Clipboard.Clear();
                else Forms.Clipboard.SetDataObject(previous, true);
            }
            catch { }
            completed.Set();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!completed.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Editor message smoke test timed out.");
    thread.Join();
    if (failure is not null) throw failure;
    Console.WriteLine("EDITOR_MESSAGE_PASS standard replacement, Enter preservation, and Backspace deletion");
    return 0;

    static void RunCase(string input, string replacement, bool pressEnter)
    {
        using var textBox = new Forms.TextBox { Multiline = true, Text = input };
        _ = textBox.Handle;
        textBox.SelectionStart = textBox.TextLength;
        var trailing = string.Empty;
        if (pressEnter)
        {
            Assert(SendInputService.SendEnter(textBox.Handle));
            trailing = "\n";
        }
        Forms.Clipboard.SetText(replacement + trailing);
        var deleteCount = input.Length + (pressEnter ? 1 : 0);
        Assert(SendInputService.SendEditSequence(textBox.Handle, deleteCount));
        Equal(NormalizeNewlines(replacement + trailing), NormalizeNewlines(textBox.Text));
    }

    static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}

static int RunUiaReplacementSmoke()
{
    Exception? failure = null;
    using var ready = new ManualResetEventSlim();
    Forms.Form? form = null;
    Forms.TextBox? valueBox = null;
    Forms.RichTextBox? richBox = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            form = new Forms.Form { ShowInTaskbar = false, Left = 100, Top = 100, Width = 300, Height = 180 };
            valueBox = new Forms.TextBox { Text = "this is s test", Dock = Forms.DockStyle.Top };
            richBox = new Forms.RichTextBox { Text = "i just hit enter" + Environment.NewLine, Dock = Forms.DockStyle.Fill };
            form.Controls.Add(richBox);
            form.Controls.Add(valueBox);
            form.Shown += (_, _) => ready.Set();
            Forms.Application.Run(form);
        }
        catch (Exception ex) { failure = ex; ready.Set(); }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    if (!ready.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("UI Automation test controls did not start.");
    if (failure is not null) throw failure;

    var automationThread = new Thread(() =>
    {
        try
        {
            if (form is null || valueBox is null || richBox is null) throw new InvalidOperationException("Test controls unavailable.");
            form.Invoke(() =>
            {
                valueBox.SelectionStart = valueBox.TextLength;
                richBox.SelectionStart = richBox.TextLength;
            });
            var valueHandle = (IntPtr)form.Invoke(new Func<IntPtr>(() => valueBox.Handle));
            var richHandle = (IntPtr)form.Invoke(new Func<IntPtr>(() => richBox.Handle));
            var valueElement = System.Windows.Automation.AutomationElement.FromHandle(valueHandle);
            if (!SendInputService.ReplaceForElement(valueElement, "this is s test", "This is a test."))
                throw new InvalidOperationException($"ValuePattern replacement failed; patterns={PatternNames(valueElement)}");
            var actualValue = (string)form.Invoke(new Func<string>(() => valueBox.Text));
            Equal("This is a test.", actualValue);
            var valueCaret = (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart));
            Equal(actualValue.Length, valueCaret);

            form.Invoke(() =>
            {
                valueBox.Text = "did it get slower then befor";
                valueBox.SelectionStart = valueBox.TextLength;
            });
            if (!SendInputService.ReplaceForElement(valueElement,
                    "did it get slower then befor", "did it get slower than before"))
                throw new InvalidOperationException("Exact non-duplicating replacement failed.");
            var exactReplacement = (string)form.Invoke(new Func<string>(() => valueBox.Text));
            Equal("did it get slower than before", exactReplacement);
            Assert(!exactReplacement.Contains("befordid", StringComparison.Ordinal));

            form.Invoke(() =>
            {
                valueBox.Text = "beginning";
                valueBox.SelectionStart = 0;
                valueBox.SelectionLength = 0;
            });
            Assert(SendInputService.TryMoveValueCaretToEnd(valueElement));
            Equal("beginning".Length, (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart)));

            form.Invoke(() =>
            {
                valueBox.Text = "beging";
                valueBox.SelectionStart = valueBox.TextLength;
                valueBox.SelectionLength = 0;
            });
            var valuePattern = (System.Windows.Automation.TextPattern)valueElement.GetCurrentPattern(
                System.Windows.Automation.TextPattern.Pattern);
            var originalCaretRange = valuePattern.GetSelection()[0].Clone();
            var temporaryCorrectionRange = valuePattern.DocumentRange.Clone();
            temporaryCorrectionRange.Select();
            Equal("beging", temporaryCorrectionRange.GetText(-1));
            SendInputService.RestoreSelectionAfterFailure(valuePattern, "beging", originalCaretRange);
            Equal("beging".Length, (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart)));
            Equal(0, (int)form.Invoke(new Func<int>(() => valueBox.SelectionLength)));

            var begingPoint = (System.Drawing.Point)form.Invoke(new Func<System.Drawing.Point>(() =>
                valueBox.PointToScreen(valueBox.GetPositionFromCharIndex(3))));
            var begingWindow = new WindowIdentity(valueHandle, Environment.ProcessId, "test.exe", "test");
            if (!ManualCorrectionService.TryCaptureFromElement(valueElement, begingWindow,
                    new System.Windows.Point(begingPoint.X, begingPoint.Y), out var begingTarget))
                throw new InvalidOperationException("Right-click capture for 'beging' failed.");
            Equal("beging", begingTarget.OriginalText);
            var begingResult = new SendInputService().ReplaceTextRangeDetailed(valueElement, begingTarget.Range,
                begingTarget.OriginalText, "beginning");
            Equal(TextReplacementResult.Applied, begingResult);
            Equal("beginning", (string)form.Invoke(new Func<string>(() => valueBox.Text)));
            Equal("beginning".Length, (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart)));

            const string commentOriginal = "this is a youtube comment with a speling mistake";
            const string commentCorrected = "this is a YouTube comment with a spelling mistake";
            form.Invoke(() =>
            {
                valueBox.Text = commentOriginal;
                valueBox.SelectionStart = 0;
                valueBox.SelectionLength = 0;
            });
            Assert(SendInputService.TryReplaceWholeValue(valueElement, commentOriginal, commentCorrected));
            Equal(commentCorrected, (string)form.Invoke(new Func<string>(() => valueBox.Text)));
            Equal(commentCorrected.Length, (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart)));

            form.Invoke(() =>
            {
                valueBox.Text = "now lets missspess";
                valueBox.SelectionStart = valueBox.TextLength;
            });
            Assert(SendInputService.SendBackspaces(valueHandle, 5));
            form.Invoke(() => valueBox.AppendText("pell"));
            var afterBackspace = (string)form.Invoke(new Func<string>(() => valueBox.Text));
            Equal("now lets misspell", afterBackspace);
            if (!SendInputService.ReplaceForElement(valueElement, "now lets misspell", "Now let's misspell."))
                throw new InvalidOperationException("Replacement after Backspace failed.");
            var correctedAfterBackspace = (string)form.Invoke(new Func<string>(() => valueBox.Text));
            Equal("Now let's misspell.", correctedAfterBackspace);
            var correctedCaret = (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart));
            Equal(correctedAfterBackspace.Length, correctedCaret);

            form.Invoke(() =>
            {
                valueBox.Text = "this needs corection  ";
                valueBox.SelectionStart = valueBox.TextLength;
            });
            if (!SendInputService.ReplaceForElement(valueElement, "this needs corection  ", "this needs correction  "))
                throw new InvalidOperationException("Trailing-space replacement failed.");
            var withTrailingSpaces = (string)form.Invoke(new Func<string>(() => valueBox.Text));
            Equal("this needs correction  ", withTrailingSpaces);
            Equal(withTrailingSpaces.Length, (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart)));

            form.Invoke(() =>
            {
                valueBox.Text = "typing resumed";
                valueBox.SelectionStart = valueBox.TextLength;
            });
            Assert(!SendInputService.ReplaceForElement(valueElement, "typing resumed", "Typing resumed.", () => false));
            Equal("typing resumed", (string)form.Invoke(new Func<string>(() => valueBox.Text)));
            Equal("typing resumed".Length, (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart)));

            form.Invoke(() =>
            {
                valueBox.Text = "please fix mistkae now";
                valueBox.SelectionStart = valueBox.TextLength;
                valueBox.SelectionLength = 0;
            });
            var wordPoint = (System.Drawing.Point)form.Invoke(new Func<System.Drawing.Point>(() =>
                valueBox.PointToScreen(valueBox.GetPositionFromCharIndex(14))));
            var testWindow = new WindowIdentity(valueHandle, Environment.ProcessId, "test.exe", "test");
            if (!ManualCorrectionService.TryCaptureFromElement(valueElement, testWindow,
                    new System.Windows.Point(wordPoint.X, wordPoint.Y), out var manualTarget))
                throw new InvalidOperationException("Right-click word targeting failed.");
            Equal("mistkae", manualTarget.OriginalText);
            var allowManualCommit = true;
            bool ManualCommitIsCurrent()
            {
                if (!allowManualCommit) return false;
                var selection = valuePattern.GetSelection();
                if (selection.Length == 1 && string.Equals(selection[0].GetText(-1), "mistkae",
                        StringComparison.Ordinal))
                {
                    allowManualCommit = false;
                    return false;
                }
                return true;
            }
            var staleManualResult = new SendInputService().ReplaceTextRangeDetailed(valueElement,
                manualTarget.Range, manualTarget.OriginalText, "mistake", ManualCommitIsCurrent,
                out var staleManualDiagnostic);
            Equal(TextReplacementResult.Stale, staleManualResult);
            Assert(staleManualDiagnostic.Contains("stale", StringComparison.Ordinal));
            Equal("please fix mistkae now", (string)form.Invoke(new Func<string>(() => valueBox.Text)));
            Equal("please fix mistkae now".Length,
                (int)form.Invoke(new Func<int>(() => valueBox.SelectionStart)));
            Equal(0, (int)form.Invoke(new Func<int>(() => valueBox.SelectionLength)));

            var manualResult = new SendInputService().ReplaceTextRangeDetailed(valueElement, manualTarget.Range,
                manualTarget.OriginalText, "mistake", out var manualDiagnostic);
            if (manualResult != TextReplacementResult.Applied)
            {
                var failedValue = (string)form.Invoke(new Func<string>(() => valueBox.Text));
                var failedSelection = ((System.Windows.Automation.TextPattern)valueElement.GetCurrentPattern(
                    System.Windows.Automation.TextPattern.Pattern)).GetSelection()[0].GetText(-1);
                throw new InvalidOperationException($"Selected-word replacement failed; result={manualResult}; diagnostic={manualDiagnostic}; value={ToCodePoints(failedValue)}; selection={ToCodePoints(failedSelection)}");
            }
            var selectedCorrection = (string)form.Invoke(new Func<string>(() => valueBox.Text));
            Equal("please fix mistake now", selectedCorrection);

            form.Invoke(() =>
            {
                valueBox.Text = "this has adress here";
                valueBox.SelectionStart = 9;
                valueBox.SelectionLength = 6;
            });
            var selectedPoint = (System.Drawing.Point)form.Invoke(new Func<System.Drawing.Point>(() =>
                valueBox.PointToScreen(valueBox.GetPositionFromCharIndex(11))));
            if (!ManualCorrectionService.TryCaptureFromElement(valueElement, testWindow,
                    new System.Windows.Point(selectedPoint.X, selectedPoint.Y), out var selectedTarget))
                throw new InvalidOperationException("Highlighted-text targeting failed.");
            Equal("adress", selectedTarget.OriginalText);

            // A still-valid captured range must never be reused after focus moves to
            // a different editor, even when that editor contains the same word.
            form.Invoke(() =>
            {
                richBox.Text = "adress";
                richBox.SelectionStart = richBox.TextLength;
                form.ActiveControl = richBox;
                richBox.Focus();
            });
            Thread.Sleep(50);
            Assert(!ManualCorrectionService.TryRefreshTarget(selectedTarget, out _,
                out var changedEditorDiagnostic));
            Equal("stage=refresh-focused-editor-changed", changedEditorDiagnostic);
            Equal("this has adress here", (string)form.Invoke(new Func<string>(() => valueBox.Text)));
            Equal("adress", (string)form.Invoke(new Func<string>(() => richBox.Text)));

            // Simulate the non-activating offer click collapsing the editor's
            // visible selection while the captured range remains valid.
            form.Invoke(() =>
            {
                richBox.Text = "i just hit enter" + Environment.NewLine;
                richBox.SelectionStart = richBox.TextLength;
                valueBox.SelectionStart = valueBox.TextLength;
                valueBox.SelectionLength = 0;
                form.ActiveControl = valueBox;
                valueBox.Focus();
            });
            Thread.Sleep(50);
            if (!ManualCorrectionService.TryRefreshTarget(selectedTarget, out var refreshedTarget,
                    out var refreshDiagnostic))
                throw new InvalidOperationException("Collapsed highlighted range could not be refreshed: " +
                                                    refreshDiagnostic);
            if (!new SendInputService().ReplaceTextRange(valueElement, refreshedTarget.Range,
                    refreshedTarget.OriginalText, "address"))
                throw new InvalidOperationException("Highlighted-text replacement failed.");
            Equal("this has address here", (string)form.Invoke(new Func<string>(() => valueBox.Text)));

            var richElement = System.Windows.Automation.AutomationElement.FromHandle(richHandle);
            if (!SendInputService.ReplaceForElement(richElement, "i just hit enter\n", "I just hit Enter.\n"))
                throw new InvalidOperationException($"TextPattern replacement failed; patterns={PatternNames(richElement)}; {DescribeTextRange(richElement, 17)}");
            var actualRich = (string)form.Invoke(new Func<string>(() => richBox.Text));
            Equal("I just hit Enter.\n", actualRich.Replace("\r\n", "\n", StringComparison.Ordinal));
            var richCaret = (int)form.Invoke(new Func<int>(() => richBox.SelectionStart));
            var richLength = (int)form.Invoke(new Func<int>(() => richBox.TextLength));
            Equal(richLength, richCaret);
        }
        catch (Exception ex) { failure = ex; }
        finally { form?.BeginInvoke(form.Close); }
    });
    automationThread.SetApartmentState(ApartmentState.STA);
    automationThread.Start();
    if (!automationThread.Join(TimeSpan.FromSeconds(35))) throw new TimeoutException("UI Automation replacement test timed out.");
    uiThread.Join(TimeSpan.FromSeconds(10));
    if (failure is not null) throw failure;
    Console.WriteLine("UIA_REPLACEMENT_PASS exact non-duplicating replacement, stale manual cancellation, focused-editor validation, collapsed-selection refresh, beging right-click correction, whole-comment fallback, failed-selection cleanup, caret preservation, Enter, and Backspace continuation");
    return 0;

    static string PatternNames(System.Windows.Automation.AutomationElement element) => string.Join(',',
        element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName));

    static string DescribeTextRange(System.Windows.Automation.AutomationElement element, int count)
    {
        if (!element.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var raw) ||
            raw is not System.Windows.Automation.TextPattern pattern) return "no-text-pattern";
        var selections = pattern.GetSelection();
        if (selections.Length != 1) return $"selections={selections.Length}";
        var caret = selections[0];
        var isDegenerate = caret.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            caret, System.Windows.Automation.Text.TextPatternRangeEndpoint.End) == 0;
        var range = caret.Clone();
        var moved = range.MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            System.Windows.Automation.Text.TextUnit.Character, -count);
        return $"degenerate={isDegenerate};moved={moved};text={ToCodePoints(range.GetText(-1))}";
    }
}

sealed class MemorySecretStore : IProviderSecretStore
{
    private readonly Dictionary<AiProviderKind, string> _values = new();
    public string? Read(AiProviderKind provider) => _values.GetValueOrDefault(provider);
    public void Write(AiProviderKind provider, string secret) => _values[provider] = secret;
    public void Delete(AiProviderKind provider) => _values.Remove(provider);
    public bool Contains(AiProviderKind provider) => _values.ContainsKey(provider);
}

sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }
    public string? RequestBody { get; private set; }
    public string? Authorization { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        RequestUri = request.RequestUri;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Authorization = request.Headers.Authorization?.ToString();
        return responseFactory(request);
    }
}

internal static class TestNative
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] internal static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BringWindowToTop(IntPtr window);
}
