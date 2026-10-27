using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Input;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Memory;
using LLMAutocorrect.Models;
using LLMAutocorrect.Providers;
using LLMAutocorrect.Security;
using LLMAutocorrect.UI;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Correction;

public sealed partial class ManualCorrectionService
{
    private readonly ICorrectionProvider _provider;
    private readonly SettingsManager _settings;
    private readonly TechnicalDictionary _dictionary;
    private readonly ProtectedTokenDetector _tokens;
    private readonly CorrectionValidator _validator;
    private readonly ApplicationExclusionManager _exclusions;
    private readonly ForegroundWindowService _foreground;
    private readonly FocusedControlService _focused;
    private readonly SendInputService _input;
    private readonly WritingMemory _memory;
    private readonly SpellingHistory _spellingHistory;
    private readonly ManualCorrectionOverlay _offer;
    private readonly StatusOverlay _status;
    private readonly DiagnosticsLogger _logger;
    private readonly InputVersionClock _clock;
    private readonly SemaphoreSlim _correctionGate = new(1, 1);

    public ManualCorrectionService(ICorrectionProvider provider, SettingsManager settings, TechnicalDictionary dictionary,
        ProtectedTokenDetector tokens, CorrectionValidator validator, ApplicationExclusionManager exclusions,
        ForegroundWindowService foreground, FocusedControlService focused, SendInputService input,
        WritingMemory memory, SpellingHistory spellingHistory,
        ManualCorrectionOverlay offer, StatusOverlay status, DiagnosticsLogger logger, InputVersionClock clock)
    {
        _provider = provider; _settings = settings; _dictionary = dictionary; _tokens = tokens; _validator = validator;
        _exclusions = exclusions; _foreground = foreground; _focused = focused; _input = input; _memory = memory;
        _spellingHistory = spellingHistory; _offer = offer; _status = status; _logger = logger;
        _clock = clock;
    }

    public void OfferAt(System.Windows.Point point)
    {
        var settings = _settings.Current;
        if (!settings.Enabled || !ProviderEndpointPolicy.IsAllowedByPrivateMode(settings) ||
            !settings.ManualRightClickCorrectionEnabled) return;
        var window = _foreground.GetCurrent();
        if (!_exclusions.IsAutocorrectAllowed(window.ProcessName)) return;
        if (!TryCapture(window, point, out var target)) return;
        _offer.Offer(target.OriginalText, point, () => CorrectAsync(target));
    }

    public void OfferCurrent()
    {
        var settings = _settings.Current;
        if (!settings.Enabled || !ProviderEndpointPolicy.IsAllowedByPrivateMode(settings)) return;
        var window = _foreground.GetCurrent();
        if (!_exclusions.IsAutocorrectAllowed(window.ProcessName)) return;
        var point = new CaretPositionService().GetCaretScreenPosition(window.WindowHandle);
        if (!TryCapture(window, null, out var target))
        {
            _status.ShowMessage("Select text or place the caret in a word first.", point);
            return;
        }
        _ = CorrectAsync(target);
    }

    public void DismissOffer() => _offer.Dismiss();

    public void ActivateOfferAt(System.Windows.Point point) => _offer.TryInvokeAt(point);

    private static bool TryCapture(WindowIdentity window, System.Windows.Point? point, out ManualCorrectionTarget target)
    {
        target = null!;
        try
        {
            var element = AutomationElement.FocusedElement;
            return element is not null && TryCaptureFromElement(element, window, point, out target);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryCaptureFromElement(AutomationElement element, WindowIdentity window,
        System.Windows.Point? point, out ManualCorrectionTarget target)
    {
        target = null!;
        try
        {
            if (element.Current.IsPassword ||
                !element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) || raw is not TextPattern pattern) return false;
            var selection = pattern.GetSelection();
            if (selection.Length != 1) return false;
            TextPatternRange range;
            var selected = selection[0].GetText(-1);
            if (selection[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selection[0], TextPatternRangeEndpoint.End) != 0 &&
                !string.IsNullOrWhiteSpace(selected) &&
                (point is null || RangeContainsPoint(selection[0], point.Value)))
            {
                range = selection[0].Clone();
                if (!TrimRange(range, selected, out selected)) return false;
            }
            else
            {
                range = point is { } location ? pattern.RangeFromPoint(location) : selection[0].Clone();
                range.ExpandToEnclosingUnit(TextUnit.Word);
                if (point is { } clicked && !RangeContainsPoint(range, clicked) &&
                    TryGetRangeFromNativePoint(element, pattern, clicked, out var nativeRange))
                    range = nativeRange;
                var expanded = range.GetText(-1);
                var match = Word().Match(expanded);
                if (!match.Success || !TrimRange(range, expanded, out selected, match.Index, match.Length)) return false;
            }
            if (selected.Length is < 2 or > 300 || selected.Any(char.IsControl)) return false;
            target = new(element, range, selected, window, string.Join('.', element.GetRuntimeId()), point);
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TrimRange(TextPatternRange range, string value, out string trimmed, int? start = null, int? length = null)
    {
        var contentStart = start ?? value.TakeWhile(char.IsWhiteSpace).Count();
        var contentLength = length ?? value.Trim().Length;
        if (contentLength <= 0) { trimmed = string.Empty; return false; }
        var trailing = value.Length - contentStart - contentLength;
        if (contentStart > 0 && range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, contentStart) != contentStart)
        { trimmed = string.Empty; return false; }
        if (trailing > 0 && range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -trailing) != -trailing)
        { trimmed = string.Empty; return false; }
        trimmed = value.Substring(contentStart, contentLength);
        return true;
    }

    private static bool RangeContainsPoint(TextPatternRange range, System.Windows.Point point)
    {
        var rectangles = range.GetBoundingRectangles();
        foreach (var rectangle in rectangles)
            if (rectangle.Contains(point)) return true;
        return false;
    }

    private static bool TryGetRangeFromNativePoint(AutomationElement element, TextPattern pattern,
        System.Windows.Point point, out TextPatternRange range)
    {
        range = null!;
        var handle = new IntPtr(element.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero) return false;
        var client = new NativeMethods.Point { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
        if (!NativeMethods.ScreenToClient(handle, ref client)) return false;
        var packed = new IntPtr((client.Y << 16) | (client.X & 0xFFFF));
        var rich = element.Current.ClassName.Contains("RICHEDIT", StringComparison.OrdinalIgnoreCase);
        var message = (uint)(rich ? NativeMethods.EmRichCharFromPos : NativeMethods.EmCharFromPos);
        if (NativeMethods.SendMessageTimeout(handle, message, UIntPtr.Zero, packed, 0x0002, 250, out var result) == IntPtr.Zero)
            return false;
        var raw = unchecked((long)result.ToUInt64());
        var index = rich ? (int)raw : (int)(raw & 0xFFFF);
        if (index < 0) return false;
        range = pattern.DocumentRange.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        if (range.Move(TextUnit.Character, index) != index) return false;
        range.ExpandToEnclosingUnit(TextUnit.Word);
        return true;
    }

    // The offer invokes this on the WPF thread. Keep editor waits off that thread
    // so the keyboard/mouse hooks can cancel a pending edit as input resumes.
    private Task CorrectAsync(ManualCorrectionTarget target)
    {
        var operationVersion = _clock.Current;
        return Task.Run(() => CorrectCoreAsync(target, operationVersion));
    }

    private async Task CorrectCoreAsync(ManualCorrectionTarget target, long operationVersion)
    {
        using var logOperation = _logger.BeginOperation("manual-correction", target.Window.ProcessName, operationVersion);
        if (!await _correctionGate.WaitAsync(0)) return;
        try
        {
            if (!ManualOperationIsCurrent(target, operationVersion)) return;
            var settings = _settings.Current;
            var preserveAddressBarStyle = ShouldPreserveAddressBarStyle(settings, _focused.GetCurrent(), target.Window.ProcessName);
            var protectedTokens = _tokens.Detect(target.OriginalText, _dictionary.Terms);
            var request = new CorrectionRequest(string.Empty, target.OriginalText, _dictionary.Terms, protectedTokens,
                CorrectionMode.Conservative, settings.CustomCorrectionInstructions, preserveAddressBarStyle,
                _memory.GetSpellingHints(target.OriginalText));
            CorrectionResult result;
            CorrectionValidation validation;
            var responseRetryAttempted = false;
            string? initialValidation = null;
            var maximumEditRatio = Math.Max(.60, settings.MaximumEditRatio);
            try
            {
                try
                {
                    _logger.MarkProviderRequest();
                    await _logger.WriteAsync("ProviderRequestStarted", new Dictionary<string, object?>
                        { ["Provider"] = settings.Provider.ToString(), ["TargetLength"] = target.OriginalText.Length });
                    result = await _provider.CorrectAsync(request, CancellationToken.None);
                }
                catch (ProviderUnavailableException ex) when (ex.IsRetryable &&
                                                               ManualOperationIsCurrent(target, operationVersion))
                {
                    _logger.SetOperationStage("provider-retry-delay");
                    await Task.Delay(settings.CorrectionRetryDelayMs);
                    if (!ManualOperationIsCurrent(target, operationVersion))
                    {
                        await _logger.WriteAsync("ManualCorrectionCancelled", new Dictionary<string, object?>
                            { ["CurrentInputVersion"] = _clock.Current, ["InputChanged"] = _clock.Current != operationVersion });
                        ShowStatus(target, "Correction cancelled because the text or cursor changed.");
                        return;
                    }
                    _logger.MarkProviderRequest();
                    await _logger.WriteAsync("ProviderRequestStarted", new Dictionary<string, object?>
                        { ["Provider"] = settings.Provider.ToString(), ["TargetLength"] = target.OriginalText.Length, ["Retry"] = true });
                    result = await _provider.CorrectAsync(request, CancellationToken.None);
                    await _logger.WriteAsync("ManualCorrectionProviderRetrySucceeded",
                        new Dictionary<string, object?> { ["Process"] = target.Window.ProcessName });
                }

                _logger.SetOperationStage("validation");
                validation = _validator.Validate(request, result, maximumEditRatio);
                if (CorrectionResponseRetry.ShouldRetry(validation, responseRetryAttempted,
                        ManualOperationIsCurrent(target, operationVersion)))
                {
                    initialValidation = validation.Reason;
                    responseRetryAttempted = true;
                    request = CorrectionResponseRetry.CreateStrictRequest(request);
                    _logger.MarkProviderRequest();
                    await _logger.WriteAsync("ProviderRequestStarted", new Dictionary<string, object?>
                        { ["Provider"] = settings.Provider.ToString(), ["TargetLength"] = target.OriginalText.Length, ["Retry"] = true });
                    result = await _provider.CorrectAsync(request, CancellationToken.None);
                    _logger.SetOperationStage("validation");
                    validation = _validator.Validate(request, result, maximumEditRatio);
                }
            }
            catch (Exception ex)
            {
                var metadata = DiagnosticMetadata.Failure(ex);
                await _logger.WriteAsync("ManualCorrectionFailed", metadata);
                ShowStatus(target, ex is ProviderUnavailableException unavailable
                    ? unavailable.UserMessage
                    : "Spelling correction is temporarily unavailable.");
                return;
            }
            if (!result.ShouldReplace)
            {
                await _logger.WriteAsync("ManualCorrectionNoChange", new Dictionary<string, object?>
                {
                    ["Uncertain"] = result.Uncertain, ["ReasonProvided"] = !string.IsNullOrWhiteSpace(result.Reason),
                    ["CurrentInputVersion"] = _clock.Current
                });
                ShowStatus(target, result.Uncertain
                    ? "Not corrected: " + CleanReason(result.Reason)
                    : "No spelling correction was needed.");
                return;
            }
            if (!validation.IsValid)
            {
                await _logger.WriteAsync("ManualCorrectionRejected", new Dictionary<string, object?>
                {
                    ["Process"] = target.Window.ProcessName,
                    ["TargetLength"] = target.OriginalText.Length,
                    ["EditRatio"] = validation.EditRatio.ToString("F3"),
                    ["Validation"] = validation.Reason,
                    ["Style"] = preserveAddressBarStyle ? "address-bar-spelling" : "standard",
                    ["ResponseRetryAttempted"] = responseRetryAttempted,
                    ["InitialValidation"] = initialValidation
                });
                ShowStatus(target, ValidationMessage(validation.Reason));
                return;
            }
            var replacement = result.Replacement.Trim();
            _logger.SetOperationStage("mutation");
            var mutation = await SafeMutationRetry.RunAsync(
                settings.InsertionRetryAttempts,
                settings.InsertionRetryDelayMs,
                () => SafeMutationRetry.CanAttempt(
                    () => ManualOperationIsCurrent(target, operationVersion), _input.WaitForPhysicalKeysReleased),
                () =>
                {
                    if (!TryRefreshTarget(target, out var applyTarget, out var refreshDiagnostic,
                            () => ManualOperationIsCurrent(target, operationVersion)))
                        return new MutationAttempt<TextReplacementResult>(TextReplacementResult.EditorUnavailable,
                            refreshDiagnostic);
                    var applyResult = _input.ReplaceTextRangeDetailed(applyTarget.Element, applyTarget.Range,
                        applyTarget.OriginalText, replacement,
                        () => ManualOperationIsCurrent(target, operationVersion) && TargetEditorIsFocused(applyTarget),
                        out var applyDiagnostic);
                    return new MutationAttempt<TextReplacementResult>(applyResult,
                        applyResult == TextReplacementResult.Applied
                            ? $"{refreshDiagnostic} | {applyDiagnostic}"
                            : applyDiagnostic);
                },
                applyResult => applyResult == TextReplacementResult.Applied,
                (applyResult, diagnostic) => applyResult == TextReplacementResult.Stale ||
                                             SendInputService.CanSafelyRetry(target.OriginalText, diagnostic),
                CancellationToken.None);
            if (!mutation.Succeeded)
            {
                var replacementResult = mutation.LastResult ?? TextReplacementResult.EditorUnavailable;
                var effectiveStopReason = replacementResult == TextReplacementResult.Stale
                    ? MutationStopReason.CurrentnessLost
                    : mutation.StopReason;
                var replacementDiagnostic = mutation.Diagnostics.Count == 0
                    ? "stage=safety-check-changed"
                    : string.Join(" || ", mutation.Diagnostics);
                var automaticallyRecovered = mutation.Diagnostics.Any(SendInputService.RecoverySucceeded);
                await _logger.WriteAsync("ManualCorrectionApplyFailed", new Dictionary<string, object?>
                {
                    ["Process"] = target.Window.ProcessName,
                    ["Result"] = replacementResult.ToString(),
                    ["TargetLength"] = target.OriginalText.Length,
                    ["Attempts"] = mutation.Attempts,
                    ["CurrentInputVersion"] = _clock.Current,
                    ["InputChanged"] = _clock.Current != operationVersion,
                    ["WindowMatches"] = _foreground.Matches(target.Window.WindowHandle, target.Window.ProcessId),
                    ["MutationStopReason"] = effectiveStopReason.ToString(),
                    ["MutationMayHaveOccurred"] = mutation.ShouldInvalidateTrackedText,
                    ["AutomaticRecovery"] = automaticallyRecovered,
                    ["Diagnostic"] = replacementDiagnostic,
                    ["ResponseRetryAttempted"] = responseRetryAttempted,
                    ["InitialValidation"] = initialValidation
                });
                ShowStatus(target, automaticallyRecovered
                    ? "The correction failed, but Spellchecker automatically restored your original text."
                    : effectiveStopReason switch
                {
                    MutationStopReason.CurrentnessLost =>
                        "Correction cancelled because new input, focus, or editor state changed.",
                    MutationStopReason.AmbiguousMutation =>
                        "The editor could not confirm whether the correction was inserted. Spellchecker stopped to avoid duplicating text.",
                    _ => ReplacementFailureMessage(replacementResult, replacementDiagnostic)
                });
                return;
            }
            await _memory.RecordCorrectionAsync(target.OriginalText, replacement, protectedTokens);
            await _spellingHistory.RecordCorrectionAsync(target.OriginalText, replacement, protectedTokens);
            ShowStatus(target, $"{target.OriginalText} → {replacement}");
            await _logger.WriteAsync("ManualCorrectionCompleted", new Dictionary<string, object?>
            {
                ["Process"] = target.Window.ProcessName,
                ["Characters"] = target.OriginalText.Length,
                ["Attempts"] = mutation.Attempts,
                ["Diagnostic"] = string.Join(" || ", mutation.Diagnostics),
                ["EditRatio"] = validation.EditRatio.ToString("F3"),
                ["ResponseRetryAttempted"] = responseRetryAttempted,
                ["InitialValidation"] = initialValidation
            });
        }
        finally { _correctionGate.Release(); }
    }

    private void ShowStatus(ManualCorrectionTarget target, string message)
    {
        var point = new CaretPositionService().GetCaretScreenPosition(target.Window.WindowHandle);
        _status.ShowMessage(message, point);
    }

    private bool ManualOperationIsCurrent(ManualCorrectionTarget target, long operationVersion) =>
        _clock.Current == operationVersion &&
        _foreground.Matches(target.Window.WindowHandle, target.Window.ProcessId);

    internal static bool TryRefreshTarget(ManualCorrectionTarget target, out ManualCorrectionTarget refreshed,
        out string diagnostic, Func<bool>? canRestoreFocus = null)
    {
        refreshed = null!;
        diagnostic = "stage=refresh-focused-element-unavailable mutationAttempted=False";
        try
        {
            if (canRestoreFocus is not null && !canRestoreFocus())
            {
                diagnostic = "stage=refresh-stale mutationAttempted=False";
                return false;
            }
            var focused = AutomationElement.FocusedElement;
            var candidates = focused is null ? [] : SendInputService.ReplacementCandidates(focused).ToArray();
            var focusRestored = false;
            if (!ContainsTargetEditor(candidates, target))
            {
                // Closing a browser context menu can put focus on the document or
                // browser chrome. The explicit manual request still names a live
                // range in one editor. Restore only that captured editor, while no
                // new input has occurred and its original window is foreground.
                if (canRestoreFocus is not null && canRestoreFocus() &&
                    NativeMethods.GetForegroundWindow() == target.Window.WindowHandle &&
                    TargetBelongsToWindow(target) && TryValidateCapturedTarget(target, out _) &&
                    canRestoreFocus())
                {
                    target.Element.SetFocus();
                    var deadline = Environment.TickCount64 + 150;
                    do
                    {
                        if (!canRestoreFocus())
                        {
                            diagnostic = "stage=refresh-stale mutationAttempted=False";
                            return false;
                        }
                        focused = AutomationElement.FocusedElement;
                        candidates = focused is null ? [] : SendInputService.ReplacementCandidates(focused).ToArray();
                        if (ContainsTargetEditor(candidates, target)) { focusRestored = true; break; }
                        if (Environment.TickCount64 >= deadline) break;
                        Thread.Sleep(10);
                    } while (true);
                }
                if (!ContainsTargetEditor(candidates, target))
                {
                    diagnostic = $"stage=refresh-focused-editor-changed mutationAttempted=False {DescribeFocus(focused, target)}";
                    return false;
                }
            }

            // Clicking the non-activating correction offer can collapse the visible
            // selection in a WebView editor. The captured TextPatternRange remains
            // usable, so prefer it when the same editor is still focused and the
            // exact original text is still present. This avoids incorrectly turning
            // a selected sentence back into only the word under the click point.
            if (TryValidateCapturedTarget(target, out refreshed))
            {
                diagnostic = $"stage=refresh-original-range-confirmed focusRestored={focusRestored}";
                return true;
            }

            foreach (var candidate in candidates)
            {
                var runtimeId = SafeRuntimeId(candidate);
                // Never search another editor for an identical word or sentence.
                if (target.FocusedElementId != runtimeId) continue;
                if (!TryCaptureFromElement(candidate, target.Window, target.CapturePoint, out var captured)) continue;
                if (!string.Equals(captured.OriginalText, target.OriginalText, StringComparison.Ordinal))
                {
                    diagnostic = "stage=refresh-text-changed mutationAttempted=False";
                    continue;
                }
                refreshed = captured;
                diagnostic = $"stage=refresh-confirmed focusRestored={focusRestored}";
                return true;
            }
            diagnostic = "stage=refresh-target-not-found mutationAttempted=False";
            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostic = $"stage=refresh-exception type={ex.GetType().Name} hresult={ex.HResult} mutationAttempted=False";
            return false;
        }
    }

    private static bool ContainsTargetEditor(IEnumerable<AutomationElement> candidates, ManualCorrectionTarget target) =>
        candidates.Any(candidate => string.Equals(SafeRuntimeId(candidate), target.FocusedElementId,
            StringComparison.Ordinal));

    internal static bool TargetEditorIsFocused(ManualCorrectionTarget target)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            return focused is not null && ContainsTargetEditor(SendInputService.ReplacementCandidates(focused), target);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or
                                   UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool TargetBelongsToWindow(ManualCorrectionTarget target)
    {
        var current = target.Element;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (new IntPtr(current.Current.NativeWindowHandle) == target.Window.WindowHandle)
                return current.Current.ProcessId == target.Window.ProcessId;
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return false;
    }

    private static string DescribeFocus(AutomationElement? focused, ManualCorrectionTarget target) =>
        $"focusedType={focused?.Current.ControlType.ProgrammaticName ?? "none"} " +
        $"focusedClass={(focused?.Current.ClassName ?? "none").Replace(' ', '_')} " +
        $"targetType={target.Element.Current.ControlType.ProgrammaticName} " +
        $"targetClass={(target.Element.Current.ClassName ?? "none").Replace(' ', '_')}";

    private static bool TryValidateCapturedTarget(ManualCorrectionTarget target,
        out ManualCorrectionTarget refreshed)
    {
        refreshed = null!;
        try
        {
            if (target.Element.Current.IsPassword ||
                !string.Equals(SafeRuntimeId(target.Element), target.FocusedElementId, StringComparison.Ordinal)) return false;
            var range = target.Range.Clone();
            if (!string.Equals(NormalizeNewlines(range.GetText(-1)), NormalizeNewlines(target.OriginalText),
                    StringComparison.Ordinal)) return false;
            refreshed = target with { Range = range };
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? SafeRuntimeId(AutomationElement element)
    {
        try { return string.Join('.', element.GetRuntimeId()); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static string ReplacementFailureMessage(TextReplacementResult result, string? diagnostic = null) =>
        diagnostic?.Contains("stage=refresh-", StringComparison.Ordinal) == true &&
        SendInputService.FailureOccurredBeforeMutation(diagnostic)
            ? "The original text field could not be reconnected safely. Nothing was changed. Click the field and try Ctrl+Alt+C."
            : result switch
    {
        TextReplacementResult.Stale =>
            "Correction cancelled because new input, focus, or editor state changed.",
        TextReplacementResult.TextChanged =>
            "The word is no longer at its original location, so no text was changed.",
        TextReplacementResult.ClipboardUnavailable =>
            "Correction could not access the clipboard. Your text and cursor were restored.",
        TextReplacementResult.SelectionUnavailable =>
            diagnostic?.Contains("focusMatch=False", StringComparison.OrdinalIgnoreCase) == true
                ? "The editor did not regain focus in time. Nothing was changed; please try again."
                : "The editor did not confirm the text selection after three attempts. Nothing was changed; please try again.",
        TextReplacementResult.PasteFailed =>
            "The correction could not be inserted. Your text and cursor were restored.",
        _ => "The editor became unavailable. No correction was applied."
    };

    private static string CleanReason(string reason) => string.IsNullOrWhiteSpace(reason)
        ? "the intended spelling was unclear."
        : reason.Trim().TrimEnd('.') + ".";

    internal static bool ShouldPreserveAddressBarStyle(AppSettings settings, FocusedControlIdentity focus, string processName) =>
        settings.BrowserAddressBarSpellingOnly && focus.IsBrowserAddressBar(processName);

    internal static string ValidationMessage(string reason) => reason switch
    {
        "edit-ratio" => "Not corrected because the suggestion changed too much text.",
        "length" => "Not corrected because the suggestion changed the text length too much.",
        "protected-token" => "Not corrected because the suggestion changed a protected name or technical term.",
        "number-changed" => "Not corrected because the suggestion changed a number.",
        "address-bar-style" => "Not corrected because address-bar spelling mode preserves capitalization and punctuation.",
        "repeated-character-run" => "Not corrected because the suggestion contained repeated characters.",
        "explanation-or-markdown" => "Not corrected because the response included extra explanatory text.",
        "empty" => "Not corrected because the suggestion was empty.",
        _ => "Not corrected because the suggested change did not pass validation."
    };

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.Compiled)]
    private static partial Regex Word();

    internal sealed record ManualCorrectionTarget(AutomationElement Element, TextPatternRange Range, string OriginalText,
        WindowIdentity Window, string FocusedElementId, System.Windows.Point? CapturePoint);
}
