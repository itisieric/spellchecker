using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;
using LLMAutocorrect.Input;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Memory;
using LLMAutocorrect.Models;
using LLMAutocorrect.Providers;
using LLMAutocorrect.Security;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Autocomplete;

internal enum AutocompleteKeyAction { IgnoreModifier, AcceptAll, Dismiss, AcceptNextWord, Cycle, Other }

public sealed class AutocompleteCoordinator : IDisposable
{
    private readonly IAutocompleteProvider _provider;
    private readonly ITerminalAutocompleteProvider _terminalProvider;
    private readonly SettingsManager _settings;
    private readonly TechnicalDictionary _dictionary;
    private readonly SuggestionValidator _validator;
    private readonly TerminalSuggestionValidator _terminalValidator;
    private readonly SensitiveContentDetector _sensitive;
    private readonly ApplicationExclusionManager _exclusions;
    private readonly ForegroundWindowService _foreground;
    private readonly FocusedControlService _focused;
    private readonly CaretPositionService _caret;
    private readonly SendInputService _input;
    private readonly TypingBuffer _buffer;
    private readonly InputVersionClock _clock;
    private readonly CorrectionCoordinator _correction;
    private readonly SuggestionState _state;
    private readonly ISuggestionPresenter _presenter;
    private readonly WritingMemory _memory;
    private readonly CommandHistoryStore _commandHistory;
    private readonly DiagnosticsLogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTerminalRequest = DateTimeOffset.MinValue;
    private string? _lastContextHash;
    private string? _lastTerminalContextHash;

    public event Action<TypingSnapshot>? TextInserted;

    public AutocompleteCoordinator(IAutocompleteProvider provider, ITerminalAutocompleteProvider terminalProvider,
        SettingsManager settings, TechnicalDictionary dictionary, SuggestionValidator validator,
        TerminalSuggestionValidator terminalValidator, SensitiveContentDetector sensitive,
        ApplicationExclusionManager exclusions, ForegroundWindowService foreground, FocusedControlService focused,
        CaretPositionService caret, SendInputService input, TypingBuffer buffer, InputVersionClock clock,
        CorrectionCoordinator correction, SuggestionState state, ISuggestionPresenter presenter,
        WritingMemory memory, CommandHistoryStore commandHistory, DiagnosticsLogger logger)
    {
        _provider = provider; _terminalProvider = terminalProvider; _settings = settings; _dictionary = dictionary;
        _validator = validator; _terminalValidator = terminalValidator; _sensitive = sensitive; _exclusions = exclusions;
        _foreground = foreground; _focused = focused; _caret = caret; _input = input; _buffer = buffer;
        _clock = clock; _correction = correction; _state = state; _presenter = presenter;
        _memory = memory; _commandHistory = commandHistory; _logger = logger;
    }

    public void Schedule(TypingSnapshot snapshot)
    {
        if (IsTerminal(snapshot.Window.ProcessName) && snapshot.BufferText.EndsWith('\n'))
        {
            var command = TerminalContext.LastCompletedCommand(snapshot.BufferText);
            if (command is not null)
            {
                var shell = TerminalContext.DetectShell(snapshot.Window.ProcessName, snapshot.Window.WindowTitle);
                _ = _commandHistory.RecordAsync(command, shell);
            }
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel(); _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }
        _ = RunScheduledAsync(snapshot, cts.Token);
    }

    public bool InterceptOrDismiss(KeyboardInputEvent input)
    {
        var state = _state.Read();
        if (!state.IsVisible) return false;
        switch (ClassifyKey(input))
        {
            case AutocompleteKeyAction.IgnoreModifier: return false;
            case AutocompleteKeyAction.AcceptAll: _ = Task.Run(AcceptAllAsync); return true;
            case AutocompleteKeyAction.Dismiss: Dismiss(); return true;
            case AutocompleteKeyAction.AcceptNextWord: _ = Task.Run(AcceptNextWordAsync); return true;
            case AutocompleteKeyAction.Cycle: Cycle(); return true;
            default: Dismiss(); return false;
        }
    }

    internal static AutocompleteKeyAction ClassifyKey(KeyboardInputEvent input)
    {
        if (GlobalKeyboardHook.IsModifierKey(input.VirtualKey)) return AutocompleteKeyAction.IgnoreModifier;
        if (input.VirtualKey == NativeMethods.VkTab && !input.Control && !input.Alt) return AutocompleteKeyAction.AcceptAll;
        if (input.VirtualKey == NativeMethods.VkEscape) return AutocompleteKeyAction.Dismiss;
        if (input.VirtualKey == NativeMethods.VkRight && input.Control) return AutocompleteKeyAction.AcceptNextWord;
        if (input.VirtualKey == NativeMethods.VkDown && input.Alt) return AutocompleteKeyAction.Cycle;
        return AutocompleteKeyAction.Other;
    }

    public void Dismiss()
    {
        _state.Clear();
        _presenter.Hide();
    }

    private async Task RunScheduledAsync(TypingSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var delay = IsTerminal(snapshot.Window.ProcessName)
                ? _settings.Current.TerminalAutocompleteIdleDelayMs
                : _settings.Current.AutocompleteIdleDelayMs;
            await Task.Delay(delay, cancellationToken);
            await ProcessAsync(snapshot, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dismiss();
            await _logger.WriteAsync("AutocompleteFailed", new Dictionary<string, object?> { ["Type"] = ex.GetType().Name });
        }
    }

    private async Task ProcessAsync(TypingSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (IsTerminal(snapshot.Window.ProcessName))
        {
            await ProcessTerminalAsync(snapshot, cancellationToken);
            return;
        }

        var settings = _settings.Current;
        if (!FeatureEnabled(settings, false)) return;
        if (!snapshot.IsSynchronized) { await LogSkipAsync(snapshot, "unsynchronized"); return; }
        if (_correction.IsBusy) { await LogSkipAsync(snapshot, "correction-busy"); return; }
        if (!_exclusions.IsAutocompleteAllowed(snapshot.Window.ProcessName)) { await LogSkipAsync(snapshot, "excluded"); return; }
        var context = snapshot.BufferText.Length <= settings.AutocompleteContextCharacters
            ? snapshot.BufferText : snapshot.BufferText[^settings.AutocompleteContextCharacters..];
        if (context.Length < settings.MinimumAutocompleteCharacters || CountWords(context) < settings.MinimumAutocompleteWords ||
            !_validator.ContextIsEligible(context) || _sensitive.ContainsSensitiveTopic(context)) return;
        if (_clock.Current != snapshot.BufferVersion) { await LogSkipAsync(snapshot, "stale-version"); return; }
        if (!_foreground.Matches(snapshot.Window.WindowHandle, snapshot.Window.ProcessId)) { await LogSkipAsync(snapshot, "foreground-changed"); return; }
        var focus = _focused.GetCurrent();
        if (focus.IsPassword) { await LogSkipAsync(snapshot, "password-field"); return; }

        var hash = Hash(context.TrimEnd());
        if (hash == _lastContextHash) return;

        var remembered = _memory.RecallPhrases(context, settings.AutocompleteCandidates)
            .Select(candidate => _validator.Normalize(context, candidate, settings.MaximumSuggestionCharacters))
            .Where(candidate => candidate is not null).Cast<string>().Distinct(StringComparer.Ordinal)
            .Take(settings.AutocompleteCandidates).ToArray();
        if (remembered.Length > 0)
        {
            _lastContextHash = hash;
            ShowSuggestions(snapshot, focus, remembered, false);
            await _logger.WriteAsync("AutocompleteCompleted", new Dictionary<string, object?>
            {
                ["Provider"] = "Memory", ["LatencyMs"] = 0,
                ["ContextChars"] = context.Length, ["Candidates"] = remembered.Length, ["Shown"] = true
            });
            return;
        }
        var interval = TimeSpan.FromMilliseconds(settings.MinimumAutocompleteRequestIntervalMs) - (DateTimeOffset.UtcNow - _lastRequest);
        if (interval > TimeSpan.Zero) await Task.Delay(interval, cancellationToken);
        _lastRequest = DateTimeOffset.UtcNow;

        var request = new AutocompleteRequest(context, RelevantTechnicalTerms(context, _dictionary.Terms), settings.AutocompleteCandidates,
            settings.AutocompleteMode == AutocompleteMode.Conservative ? 6 : settings.AutocompleteMode == AutocompleteMode.Normal ? 10 : 16,
            settings.AutocompleteMode);
        var stopwatch = Stopwatch.StartNew();
        var result = await _provider.PredictAsync(request, cancellationToken);
        _lastContextHash = hash;
        stopwatch.Stop();
        if (!StillCurrent(snapshot, focus)) return;

        var candidates = result.Candidates.Select(x => _validator.Normalize(context, x.Text, settings.MaximumSuggestionCharacters))
            .Where(x => x is not null).Cast<string>().Distinct(StringComparer.Ordinal).Take(settings.AutocompleteCandidates).ToArray();
        if (candidates.Length == 0) return;
        ShowSuggestions(snapshot, focus, candidates, false);
        await _logger.WriteAsync("AutocompleteCompleted", new Dictionary<string, object?>
        {
            ["Provider"] = ProviderClient.DisplayName(settings.Provider), ["LatencyMs"] = stopwatch.ElapsedMilliseconds,
            ["ContextChars"] = context.Length, ["Candidates"] = candidates.Length, ["Shown"] = true
        });
    }

    private async Task ProcessTerminalAsync(TypingSnapshot snapshot, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!FeatureEnabled(settings, true)) return;
        if (!snapshot.IsSynchronized) { await LogTerminalSkipAsync(snapshot, "unsynchronized"); return; }
        var currentCommand = TerminalContext.CurrentLine(snapshot.BufferText);
        if (currentCommand.Length < settings.TerminalMinimumCharacters || !_terminalValidator.IsSafeContext(currentCommand)) return;
        if (_clock.Current != snapshot.BufferVersion) { await LogTerminalSkipAsync(snapshot, "stale-version"); return; }
        if (!_foreground.Matches(snapshot.Window.WindowHandle, snapshot.Window.ProcessId)) { await LogTerminalSkipAsync(snapshot, "foreground-changed"); return; }
        var focus = _focused.GetCurrent();
        if (focus.IsPassword) { await LogTerminalSkipAsync(snapshot, "password-field"); return; }

        var shell = TerminalContext.DetectShell(snapshot.Window.ProcessName, snapshot.Window.WindowTitle);
        var hash = Hash(shell + "|" + currentCommand);
        if (hash == _lastTerminalContextHash) return;
        var maximum = Math.Clamp(settings.TerminalAutocompleteCandidates, 1, 10);
        var matchingHistory = _commandHistory.GetMatchingCommands(shell, currentCommand, 30);
        var historyCandidates = matchingHistory
            .Select(command => _terminalValidator.Normalize(currentCommand, command))
            .Where(candidate => candidate is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(maximum).ToList();

        if (historyCandidates.Count > 0) ShowSuggestions(snapshot, focus, historyCandidates, true);
        if (historyCandidates.Count >= maximum)
        {
            _lastTerminalContextHash = hash;
            await LogTerminalCompletedAsync("History", 0, currentCommand.Length, historyCandidates.Count);
            return;
        }

        var interval = TimeSpan.FromMilliseconds(settings.MinimumTerminalRequestIntervalMs) -
                       (DateTimeOffset.UtcNow - _lastTerminalRequest);
        if (interval > TimeSpan.Zero) await Task.Delay(interval, cancellationToken);
        _lastTerminalRequest = DateTimeOffset.UtcNow;

        var recent = TerminalContext.RecentSessionCommands(snapshot.BufferText, 12)
            .Where(command => !string.Equals(command, currentCommand, StringComparison.Ordinal) &&
                              _terminalValidator.IsSafeHistoryEntry(command))
            .ToArray();
        var request = new TerminalAutocompleteRequest(shell, currentCommand,
            snapshot.Window.WindowTitle.Length > 200 ? snapshot.Window.WindowTitle[..200] : snapshot.Window.WindowTitle,
            recent, matchingHistory.Take(20).ToArray(), maximum, settings.TerminalCustomInstructions);
        var stopwatch = Stopwatch.StartNew();
        AutocompleteResult result;
        try { result = await _terminalProvider.PredictAsync(request, cancellationToken); }
        catch when (historyCandidates.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            _lastTerminalContextHash = hash;
            await LogTerminalCompletedAsync("HistoryFallback", stopwatch.ElapsedMilliseconds, currentCommand.Length, historyCandidates.Count);
            return;
        }
        stopwatch.Stop();
        _lastTerminalContextHash = hash;
        if (!StillCurrent(snapshot, focus)) return;

        var candidates = historyCandidates.Concat(result.Candidates
                .Select(candidate => _terminalValidator.Normalize(currentCommand, candidate.Text))
                .Where(candidate => candidate is not null).Cast<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(maximum).ToArray();
        if (candidates.Length == 0) return;
        ShowSuggestions(snapshot, focus, candidates, true);
        await LogTerminalCompletedAsync(ProviderClient.DisplayName(settings.Provider) + "+History",
            stopwatch.ElapsedMilliseconds, currentCommand.Length, candidates.Length);
    }

    private async Task AcceptAllAsync()
    {
        var state = _state.Read();
        if (!CanInsert(state)) { Dismiss(); return; }
        await InsertAsync(state, state.CurrentText, string.Empty);
    }

    private async Task AcceptNextWordAsync()
    {
        var state = _state.Read();
        if (!CanInsert(state)) { Dismiss(); return; }
        var current = state.CurrentText;
        var leading = current.Length - current.TrimStart().Length;
        var wordEnd = current.IndexOfAny([' ', '\t', '\r', '\n'], leading);
        var take = wordEnd < 0 ? current.Length : wordEnd + 1;
        await InsertAsync(state, current[..take], current[take..]);
    }

    private async Task InsertAsync(SuggestionState.StateData state, string inserted, string remaining)
    {
        if (!SafeMutationRetry.CanAttempt(() => CanInsert(state), _input.WaitForPhysicalKeysReleased))
        {
            Dismiss();
            return;
        }
        var contextBefore = _buffer.GetSnapshot().BufferText;
        var mutation = await SafeMutationRetry.RunAsync(
            _settings.Current.InsertionRetryAttempts,
            _settings.Current.InsertionRetryDelayMs,
            () => SafeMutationRetry.CanAttempt(() => CanInsert(state), _input.WaitForPhysicalKeysReleased),
            () =>
            {
                var applied = _input.InsertText(inserted, () => CanInsert(state));
                return new MutationAttempt<bool>(applied, _input.LastFailureDiagnostic);
            },
            applied => applied,
            (_, diagnostic) => SendInputService.CanSafelyRetry(string.Empty, diagnostic),
            CancellationToken.None);
        if (!mutation.Succeeded)
        {
            await _logger.WriteAsync(state.IsTerminal
                ? "TerminalAutocompleteInsertionFailed"
                : "AutocompleteInsertionFailed", new Dictionary<string, object?>
            {
                ["Attempts"] = mutation.Attempts,
                ["MutationStopReason"] = mutation.StopReason.ToString(),
                ["MutationMayHaveOccurred"] = mutation.ShouldInvalidateTrackedText,
                ["Diagnostic"] = string.Join(" || ", mutation.Diagnostics)
            });
            // If verification could not prove whether an insertion happened, stop
            // trusting the in-memory suffix. The next physical character starts a
            // fresh synchronized segment instead of risking a later bad edit.
            if (mutation.ShouldInvalidateTrackedText)
            {
                var invalidatedVersion = _clock.Increment();
                _buffer.Invalidate(_foreground.GetCurrent(), invalidatedVersion);
            }
            Dismiss();
            return;
        }
        var nextVersion = _clock.Increment();
        if (!_buffer.TryAppend(state.BufferVersion, inserted, nextVersion)) { Dismiss(); return; }
        if (remaining.Length == 0)
        {
            Dismiss();
        }
        else
        {
            var candidates = state.Candidates.ToArray();
            candidates[state.SelectedCandidateIndex] = remaining;
            var next = state with
            {
                BufferVersion = nextVersion,
                Candidates = candidates,
                AcceptedCharacterCount = state.AcceptedCharacterCount + inserted.Length
            };
            _state.Set(next);
            _presenter.Show(next.CurrentText, next.SelectedCandidateIndex, next.Candidates.Count,
                _caret.GetCaretScreenPosition(next.WindowHandle), next.IsTerminal);
        }
        if (!state.IsTerminal) await _memory.RecordAcceptedAsync(contextBefore, inserted);
        await _logger.WriteAsync(state.IsTerminal ? "TerminalAutocompleteAccepted" : "AutocompleteAccepted",
            new Dictionary<string, object?> { ["Characters"] = inserted.Length, ["Partial"] = remaining.Length > 0 });
        TextInserted?.Invoke(_buffer.GetSnapshot());
    }

    private bool CanInsert(SuggestionState.StateData state)
    {
        if (!state.IsVisible || _clock.Current != state.BufferVersion || !_foreground.Matches(state.WindowHandle, state.ProcessId)) return false;
        var focus = _focused.GetCurrent();
        return !focus.IsPassword && (state.FocusedElementId is null || focus.RuntimeId is null || state.FocusedElementId == focus.RuntimeId);
    }

    private void Cycle()
    {
        var state = _state.Read();
        if (!state.IsVisible || state.Candidates.Count < 2) return;
        var next = state with { SelectedCandidateIndex = (state.SelectedCandidateIndex + 1) % state.Candidates.Count };
        _state.Set(next);
        _presenter.Show(next.CurrentText, next.SelectedCandidateIndex, next.Candidates.Count,
            _caret.GetCaretScreenPosition(next.WindowHandle), next.IsTerminal);
    }

    private bool StillCurrent(TypingSnapshot snapshot, FocusedControlIdentity originalFocus)
    {
        if (_clock.Current != snapshot.BufferVersion || !_foreground.Matches(snapshot.Window.WindowHandle, snapshot.Window.ProcessId)) return false;
        var currentFocus = _focused.GetCurrent();
        return !currentFocus.IsPassword && (originalFocus.RuntimeId is null || currentFocus.RuntimeId is null ||
                                             originalFocus.RuntimeId == currentFocus.RuntimeId);
    }

    private bool IsTerminal(string processName) =>
        TerminalContext.IsTerminalProcess(processName, _settings.Current.TerminalProcesses);

    internal static bool FeatureEnabled(AppSettings settings, bool terminal) =>
        ProviderEndpointPolicy.IsAllowedByPrivateMode(settings) &&
        (terminal ? settings.TerminalAutocompleteEnabled : settings.AutocompleteEnabled);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int CountWords(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    internal static IReadOnlyList<string> RelevantTechnicalTerms(string context, IEnumerable<string> terms) =>
        terms.Where(term => term.Length > 0 && context.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();

    private void ShowSuggestions(TypingSnapshot snapshot, FocusedControlIdentity focus,
        IReadOnlyList<string> candidates, bool isTerminal)
    {
        var state = new SuggestionState.StateData(true, snapshot.BufferVersion, snapshot.Window.ProcessId,
            snapshot.Window.WindowHandle, focus.RuntimeId, candidates, 0, 0, isTerminal);
        _state.Set(state);
        _presenter.Show(state.CurrentText, 0, candidates.Count,
            _caret.GetCaretScreenPosition(snapshot.Window.WindowHandle), isTerminal);
    }

    private Task LogSkipAsync(TypingSnapshot snapshot, string reason) => _logger.WriteAsync("AutocompleteSkipped",
        new Dictionary<string, object?>
        {
            ["Process"] = snapshot.Window.ProcessName,
            ["ContextLength"] = snapshot.BufferText.Length,
            ["Reason"] = reason
        });

    private Task LogTerminalSkipAsync(TypingSnapshot snapshot, string reason) => _logger.WriteAsync("TerminalAutocompleteSkipped",
        new Dictionary<string, object?>
        {
            ["Process"] = snapshot.Window.ProcessName,
            ["ContextLength"] = snapshot.BufferText.Length,
            ["Reason"] = reason
        });

    private Task LogTerminalCompletedAsync(string provider, long latencyMs, int contextLength, int candidates) =>
        _logger.WriteAsync("TerminalAutocompleteCompleted", new Dictionary<string, object?>
        {
            ["Provider"] = provider,
            ["LatencyMs"] = latencyMs,
            ["ContextChars"] = contextLength,
            ["Candidates"] = candidates,
            ["Shown"] = true
        });

    public void Dispose()
    {
        lock (_gate) { _pending?.Cancel(); _pending?.Dispose(); _pending = null; }
        Dismiss();
    }
}
