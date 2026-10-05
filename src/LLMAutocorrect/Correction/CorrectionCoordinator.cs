using System.Diagnostics;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Input;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Memory;
using LLMAutocorrect.Models;
using LLMAutocorrect.Security;
using LLMAutocorrect.UI;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Correction;

public sealed class CorrectionCoordinator : IDisposable
{
    private readonly ICorrectionProvider _provider;
    private readonly SettingsManager _settings;
    private readonly TechnicalDictionary _dictionary;
    private readonly ProtectedTokenDetector _tokenDetector;
    private readonly CorrectionValidator _validator;
    private readonly ApplicationExclusionManager _exclusions;
    private readonly ForegroundWindowService _foreground;
    private readonly FocusedControlService _focusedControl;
    private readonly SendInputService _input;
    private readonly TypingBuffer _buffer;
    private readonly InputVersionClock _clock;
    private readonly CorrectionHistory _history;
    private readonly WritingMemory _memory;
    private readonly SpellingHistory _spellingHistory;
    private readonly StatusOverlay _status;
    private readonly CaretPositionService _caret;
    private readonly DiagnosticsLogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _busy;

    public event Action<TypingSnapshot>? Completed;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public CorrectionCoordinator(ICorrectionProvider provider, SettingsManager settings, TechnicalDictionary dictionary,
        ProtectedTokenDetector tokenDetector, CorrectionValidator validator, ApplicationExclusionManager exclusions,
        ForegroundWindowService foreground, FocusedControlService focusedControl, SendInputService input,
        TypingBuffer buffer, InputVersionClock clock, CorrectionHistory history, WritingMemory memory,
        SpellingHistory spellingHistory, StatusOverlay status, CaretPositionService caret, DiagnosticsLogger logger)
    {
        _provider = provider; _settings = settings; _dictionary = dictionary; _tokenDetector = tokenDetector;
        _validator = validator; _exclusions = exclusions; _foreground = foreground; _focusedControl = focusedControl;
        _input = input; _buffer = buffer; _clock = clock; _history = history; _memory = memory;
        _spellingHistory = spellingHistory; _status = status; _caret = caret; _logger = logger;
    }

    public void Schedule(TypingSnapshot snapshot, bool immediate = false)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }
        _ = RunScheduledAsync(snapshot, immediate, cts.Token);
    }

    public void Cancel()
    {
        lock (_gate) _pending?.Cancel();
    }

    private async Task RunScheduledAsync(TypingSnapshot snapshot, bool immediate, CancellationToken cancellationToken)
    {
        var processed = false;
        try
        {
            if (!immediate) await Task.Delay(_settings.Current.IdleDelayMs, cancellationToken);
            processed = true;
            await ProcessAsync(snapshot, cancellationToken, false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _logger.WriteAsync("CorrectionFailed", new Dictionary<string, object?> { ["Type"] = ex.GetType().Name });
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_settings.Current.CorrectionRetryDelayMs, cancellationToken);
                    if (_clock.Current == snapshot.BufferVersion)
                        await ProcessAsync(snapshot, cancellationToken, true);
                }
                catch (OperationCanceledException) { }
                catch (Exception retryEx)
                {
                    await _logger.WriteAsync("CorrectionRetryFailed", new Dictionary<string, object?> { ["Type"] = retryEx.GetType().Name });
                    ShowStatus(snapshot.Window.WindowHandle, "Spelling correction is temporarily unavailable.");
                }
            }
        }
        finally { if (processed && !cancellationToken.IsCancellationRequested) Completed?.Invoke(_buffer.GetSnapshot()); }
    }

    private async Task ProcessAsync(TypingSnapshot snapshot, CancellationToken cancellationToken, bool isRetry)
    {
        var settings = _settings.Current;
        if (!settings.Enabled) { await LogSkipAsync(snapshot, "disabled"); return; }
        if (settings.PrivateMode) { await LogSkipAsync(snapshot, "private-mode"); return; }
        if (!snapshot.IsSynchronized) { await LogSkipAsync(snapshot, "unsynchronized"); return; }
        if (!_exclusions.IsAutocorrectAllowed(snapshot.Window.ProcessName)) { await LogSkipAsync(snapshot, "excluded"); return; }
        var extracted = ExtractTarget(snapshot.BufferText, settings.MaximumTargetCharacters, settings.MaximumContextCharacters);
        if (extracted.Target.Length < settings.MinimumCharacters) return;
        if (CountWords(extracted.Target) < settings.MinimumWords) return;
        if (_clock.Current != snapshot.BufferVersion) { await LogSkipAsync(snapshot, "stale-version"); return; }
        if (!_foreground.Matches(snapshot.Window.WindowHandle, snapshot.Window.ProcessId)) { await LogSkipAsync(snapshot, "foreground-changed"); return; }

        var focus = _focusedControl.GetCurrent();
        if (focus.IsPassword) { await LogSkipAsync(snapshot, "password-field"); return; }
        var suffixToReplace = extracted.Target + extracted.Trailing;
        var requestSnapshot = new CorrectionSnapshot(snapshot.BufferVersion, snapshot.Window.WindowHandle,
            snapshot.Window.ProcessId, focus.RuntimeId, suffixToReplace, DateTimeOffset.UtcNow);
        var protectedTokens = _tokenDetector.Detect(extracted.Target, _dictionary.Terms);
        var addressBarStyle = settings.BrowserAddressBarSpellingOnly && focus.IsBrowserAddressBar(snapshot.Window.ProcessName);
        var request = new CorrectionRequest(extracted.Context, extracted.Target, _dictionary.Terms, protectedTokens,
            settings.Mode, settings.CustomCorrectionInstructions, addressBarStyle, _memory.GetSpellingHints(extracted.Target));

        var interval = TimeSpan.FromMilliseconds(settings.MinimumRequestIntervalMs) - (DateTimeOffset.UtcNow - _lastRequest);
        if (interval > TimeSpan.Zero) await Task.Delay(interval, cancellationToken);
        _lastRequest = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _busy, 1);
        var stopwatch = Stopwatch.StartNew();
        CorrectionResult result;
        try { result = await _provider.CorrectAsync(request, cancellationToken); }
        finally { Interlocked.Exchange(ref _busy, 0); }
        stopwatch.Stop();

        if (!result.ShouldReplace && result.Uncertain)
        {
            if (!isRetry)
            {
                await Task.Delay(settings.CorrectionRetryDelayMs, cancellationToken);
                if (StillCurrent(requestSnapshot)) await ProcessAsync(snapshot, cancellationToken, true);
            }
            else
            {
                await _logger.WriteAsync("CorrectionUncertain", new Dictionary<string, object?>
                {
                    ["Process"] = snapshot.Window.ProcessName, ["TargetLength"] = extracted.Target.Length,
                    ["Reason"] = string.IsNullOrWhiteSpace(result.Reason) ? "unclear" : result.Reason
                });
                ShowStatus(snapshot.Window.WindowHandle, "Not corrected: " + CleanReason(result.Reason));
            }
            return;
        }

        var validation = _validator.Validate(request, result, settings.MaximumEditRatio);
        var metadata = new Dictionary<string, object?>
        {
            ["Process"] = snapshot.Window.ProcessName, ["TargetLength"] = extracted.Target.Length,
            ["LatencyMs"] = stopwatch.ElapsedMilliseconds, ["EditRatio"] = validation.EditRatio.ToString("F3"),
            ["Validation"] = validation.Reason,
            ["Style"] = addressBarStyle ? "address-bar-spelling" : "standard"
        };
        if (!validation.IsValid)
        {
            await _logger.WriteAsync("CorrectionRejected", metadata);
            if (validation.Reason is "edit-ratio" or "length" or "repeated-character-run")
                ShowStatus(snapshot.Window.WindowHandle, "Correction skipped because the suggested edit was not safe.");
            return;
        }
        // The model can finish at the exact moment the user resumes typing. Require
        // one final quiet window before touching the editor; any new key cancels this
        // scheduled correction and the newer snapshot gets its own request.
        await Task.Delay(settings.CorrectionCommitDelayMs, cancellationToken);
        if (!StillCurrent(requestSnapshot)) { metadata["Result"] = "stale"; await _logger.WriteAsync("CorrectionDiscarded", metadata); return; }
        if (!_input.WaitForPhysicalKeysReleased() || !StillCurrent(requestSnapshot))
        {
            metadata["Result"] = "physical-key-or-stale";
            await _logger.WriteAsync("CorrectionDiscarded", metadata);
            return;
        }

        var replacement = result.Replacement.TrimEnd() + extracted.Trailing;
        var mutationAttempts = 1;
        var applied = _input.ReplacePreviousText(requestSnapshot.OriginalTargetText, replacement,
            () => StillCurrent(requestSnapshot));
        if (!applied)
        {
            await Task.Delay(80, cancellationToken);
            if (StillCurrent(requestSnapshot) && _input.WaitForPhysicalKeysReleased())
            {
                mutationAttempts++;
                applied = _input.ReplacePreviousText(requestSnapshot.OriginalTargetText, replacement,
                    () => StillCurrent(requestSnapshot));
            }
        }
        metadata["MutationAttempts"] = mutationAttempts;
        if (!applied)
        {
            metadata["Result"] = "send-input-failed";
            metadata["MutationDiagnostic"] = _input.LastFailureDiagnostic;
            await _logger.WriteAsync("CorrectionFailed", metadata);
            ShowStatus(snapshot.Window.WindowHandle,
                "The correction could not be inserted. Your text and cursor were restored.", true);
            return;
        }

        var nextVersion = _clock.Increment();
        if (!_buffer.TryReplaceSuffix(requestSnapshot.BufferVersion, requestSnapshot.OriginalTargetText, replacement, nextVersion))
        {
            await _logger.WriteAsync("CorrectionBufferResync", metadata);
            _buffer.Invalidate(_foreground.GetCurrent(), _clock.Current);
            return;
        }
        _history.Add(new(requestSnapshot.OriginalTargetText, replacement, requestSnapshot.WindowHandle,
            requestSnapshot.ProcessId, requestSnapshot.FocusedElementId, DateTimeOffset.UtcNow));
        metadata["Result"] = "applied";
        await _logger.WriteAsync("CorrectionCompleted", metadata);
        await _memory.RecordCorrectionAsync(extracted.Target, result.Replacement, protectedTokens);
        await _spellingHistory.RecordCorrectionAsync(extracted.Target, result.Replacement, protectedTokens);
        if (settings.ShowNotifications)
        {
            var pair = SpellingHistory.ExtractPairs(extracted.Target, result.Replacement, protectedTokens).FirstOrDefault();
            ShowStatus(snapshot.Window.WindowHandle, pair is null ? "Spelling corrected." : $"{pair.Misspelled} → {pair.Correct}", true);
        }
    }

    private bool StillCurrent(CorrectionSnapshot snapshot)
    {
        if (_clock.Current != snapshot.BufferVersion || !_foreground.Matches(snapshot.WindowHandle, snapshot.ProcessId)) return false;
        var currentBuffer = _buffer.GetSnapshot();
        if (currentBuffer.BufferVersion != snapshot.BufferVersion ||
            !currentBuffer.BufferText.EndsWith(snapshot.OriginalTargetText, StringComparison.Ordinal)) return false;
        var focus = _focusedControl.GetCurrent();
        return !focus.IsPassword && (snapshot.FocusedElementId is null || focus.RuntimeId is null || snapshot.FocusedElementId == focus.RuntimeId);
    }

    public async Task<bool> UndoLatestAsync(long? expectedInputVersion = null)
    {
        if (expectedInputVersion is not null && _clock.Current != expectedInputVersion.Value) return false;
        var entry = _history.Peek();
        if (entry is null || !_foreground.Matches(entry.WindowHandle, entry.ProcessId)) return false;
        var focus = _focusedControl.GetCurrent();
        if (focus.IsPassword || (entry.FocusedElementId is not null && focus.RuntimeId is not null && entry.FocusedElementId != focus.RuntimeId)) return false;
        var snapshot = _buffer.GetSnapshot();
        if (expectedInputVersion is not null && _clock.Current != expectedInputVersion.Value) return false;
        if (!snapshot.BufferText.EndsWith(entry.CorrectedText, StringComparison.Ordinal)) return false;
        if (!_input.ReplacePreviousText(entry.CorrectedText, entry.OriginalText)) return false;
        var next = _clock.Increment();
        if (!_buffer.TryReplaceSuffix(snapshot.BufferVersion, entry.CorrectedText, entry.OriginalText, next)) return false;
        _history.RemoveLatest();
        await _logger.WriteAsync("CorrectionUndo", new Dictionary<string, object?> { ["Length"] = entry.OriginalText.Length });
        return true;
    }

    internal static CorrectionTarget ExtractTarget(string text, int maxTarget, int maxContext)
    {
        if (text.Length == 0) return new(string.Empty, string.Empty, string.Empty);
        var targetEnd = text.Length;
        // Whitespace typed after the last word belongs behind the correction. If it
        // is included in the model target, TrimEnd removes it and leaves the caret
        // touching the corrected word instead of after the user's space.
        while (targetEnd > 0 && char.IsWhiteSpace(text[targetEnd - 1])) targetEnd--;
        var trailing = text[targetEnd..];
        var searchEnd = targetEnd - 1;
        if (searchEnd >= 0 && text[searchEnd] is '.' or '?' or '!') searchEnd--;
        var boundary = -1;
        for (var i = searchEnd; i >= 0; i--)
            if (text[i] is '.' or '?' or '!' or '\n') { boundary = i; break; }
        var start = boundary + 1;
        while (start < targetEnd && char.IsWhiteSpace(text[start])) start++;
        start = Math.Max(start, targetEnd - maxTarget);
        var target = text[start..targetEnd];
        var contextStart = Math.Max(0, start - maxContext);
        return new(text[contextStart..start], target, trailing);
    }

    private static int CountWords(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void ShowStatus(IntPtr windowHandle, string message, bool force = false)
    {
        if (!force && !_settings.Current.ShowUncertainCorrectionNotifications) return;
        _status.ShowMessage(message, _caret.GetCaretScreenPosition(windowHandle));
    }

    private static string CleanReason(string reason) => string.IsNullOrWhiteSpace(reason)
        ? "the intended spelling was unclear."
        : reason.Trim().TrimEnd('.') + ".";

    private Task LogSkipAsync(TypingSnapshot snapshot, string reason) => _logger.WriteAsync("CorrectionSkipped",
        new Dictionary<string, object?>
        {
            ["Process"] = snapshot.Window.ProcessName,
            ["TargetLength"] = snapshot.BufferText.Length,
            ["Reason"] = reason
        });

    public void Dispose()
    {
        lock (_gate) { _pending?.Cancel(); _pending?.Dispose(); _pending = null; }
    }
}

internal sealed record CorrectionTarget(string Context, string Target, string Trailing);
