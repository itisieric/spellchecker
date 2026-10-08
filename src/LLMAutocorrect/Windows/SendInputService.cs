using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using LLMAutocorrect.Input;

namespace LLMAutocorrect.Windows;

internal enum TextReplacementResult
{
    Applied,
    Stale,
    TextChanged,
    ClipboardUnavailable,
    SelectionUnavailable,
    PasteFailed,
    EditorUnavailable
}

public sealed class SendInputService
{
    private const string ExcludeClipboardFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const int SelectionAttempts = 3;
    private const int SelectionAttemptTimeoutMs = 100;
    private const int ClipboardAttempts = 5;
    private const int ClipboardRetryDelayMs = 20;
    private readonly PhysicalKeyState _physicalKeys;
    private int _isInjecting;
    private string? _lastFailureDiagnostic;
    public bool IsInjecting => Volatile.Read(ref _isInjecting) != 0;
    public string? LastFailureDiagnostic => Volatile.Read(ref _lastFailureDiagnostic);
    public string PhysicalKeyDiagnostic => _physicalKeys.LastWaitDiagnostic;

    public SendInputService(PhysicalKeyState? physicalKeys = null) => _physicalKeys = physicalKeys ?? new PhysicalKeyState();

    public bool WaitForPhysicalKeysReleased() => _physicalKeys.WaitUntilReleased(TimeSpan.FromMilliseconds(750));

    public bool ReplacePreviousText(string original, string replacement, Func<bool>? canCommit = null)
    {
        Interlocked.Exchange(ref _isInjecting, 1);
        Volatile.Write(ref _lastFailureDiagnostic, null);
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword)
            {
                Volatile.Write(ref _lastFailureDiagnostic, "stage=focused-element unavailable-or-password");
                return false;
            }
            if (canCommit is not null && !canCommit())
            {
                Volatile.Write(ref _lastFailureDiagnostic, "stage=pre-commit stale");
                return false;
            }
            var diagnostics = new List<string>();
            var depth = 0;
            foreach (var candidate in ReplacementCandidates(element))
            {
                if (canCommit is not null && !canCommit())
                {
                    diagnostics.Add($"candidate={depth} stage=pre-commit-stale");
                    break;
                }
                string? candidateDiagnostic = null;
                if (ReplaceForElement(candidate, original, replacement, canCommit,
                        detail => candidateDiagnostic = detail)) return true;
                diagnostics.Add($"candidate={depth} class={SafeClassName(candidate)} " +
                                (candidateDiagnostic ?? "stage=no-supported-edit-pattern"));
                if (!FailureOccurredBeforeMutation(candidateDiagnostic)) break;
                depth++;
            }
            Volatile.Write(ref _lastFailureDiagnostic, string.Join(" | ", diagnostics));
            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            Volatile.Write(ref _lastFailureDiagnostic,
                $"stage=exception type={ex.GetType().Name} hresult={ex.HResult}");
            return false;
        }
        finally { Interlocked.Exchange(ref _isInjecting, 0); }
    }

    public bool InsertText(string text, Func<bool>? canCommit = null) =>
        ReplacePreviousText(string.Empty, text, canCommit);

    internal static bool CanSafelyRetry(string original, string? diagnostic)
    {
        // Retry only when the failure occurred before mutation or the editor
        // explicitly confirmed that the exact original selection survived a paste
        // attempt. Merely having non-empty original text is not enough: a stale UIA
        // provider could briefly report old text after an ambiguous paste.
        return FailureOccurredBeforeMutation(diagnostic) ||
               diagnostic?.Contains("original-selection-confirmed=True", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool FailureOccurredBeforeMutation(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return false;
        if (diagnostic.Contains("mutationAttempted=False", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("refresh-exception", StringComparison.OrdinalIgnoreCase)) return true;
        if (diagnostic.Contains("paste", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("exception", StringComparison.OrdinalIgnoreCase)) return false;
        return diagnostic.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("readonly", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("selection", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("suffix-mismatch", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("text-mismatch", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("refresh-target-not-found", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("range-short", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("no-supported-edit-pattern", StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<AutomationElement> ReplacementCandidates(AutomationElement focused)
    {
        yield return focused;
        var current = focused;
        for (var depth = 0; depth < 5; depth++)
        {
            current = TryGetParent(current);
            if (current is null) yield break;
            if (SupportsTextPattern(current)) yield return current;
        }
    }

    private static AutomationElement? TryGetParent(AutomationElement element)
    {
        try { return TreeWalker.RawViewWalker.GetParent(element); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return null;
        }
    }

    private static bool SupportsTextPattern(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword) return false;
            return element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return false;
        }
    }

    private static string SafeClassName(AutomationElement element)
    {
        try { return (element.Current.ClassName ?? string.Empty).Replace(' ', '_'); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return "unavailable";
        }
    }

    public bool ReplaceTextRange(AutomationElement element, TextPatternRange range, string original, string replacement)
        => ReplaceTextRangeDetailed(element, range, original, replacement) == TextReplacementResult.Applied;

    internal TextReplacementResult ReplaceTextRangeDetailed(AutomationElement element, TextPatternRange range,
        string original, string replacement)
        => ReplaceTextRangeDetailed(element, range, original, replacement, null, out _);

    internal TextReplacementResult ReplaceTextRangeDetailed(AutomationElement element, TextPatternRange range,
        string original, string replacement, out string diagnostic)
        => ReplaceTextRangeDetailed(element, range, original, replacement, null, out diagnostic);

    internal TextReplacementResult ReplaceTextRangeDetailed(AutomationElement element, TextPatternRange range,
        string original, string replacement, Func<bool>? canCommit, out string diagnostic)
    {
        diagnostic = "stage=not-started";
        Interlocked.Exchange(ref _isInjecting, 1);
        ClipboardSnapshot? clipboard = null;
        TextPattern? pattern = null;
        TextPatternRange? originalSelection = null;
        var selectedByUs = false;
        var applied = false;
        var mutationAttempted = false;
        try
        {
            if (canCommit is not null && !canCommit())
            {
                diagnostic = "stage=manual-preflight-stale mutationAttempted=False";
                return TextReplacementResult.Stale;
            }
            if (element.Current.IsPassword || !element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) ||
                raw is not TextPattern textPattern)
            {
                diagnostic = "stage=text-pattern unavailable-or-password";
                return TextReplacementResult.EditorUnavailable;
            }
            pattern = textPattern;
            if (!string.Equals(NormalizeNewlines(range.GetText(-1)), NormalizeNewlines(original),
                    StringComparison.Ordinal))
            {
                diagnostic = "stage=initial-range text-mismatch";
                return TextReplacementResult.TextChanged;
            }
            var selections = pattern.GetSelection();
            if (selections.Length == 1) originalSelection = selections[0].Clone();
            if (!TryPrepareClipboard(NormalizeNewlines(replacement), out clipboard))
            {
                diagnostic = "stage=clipboard unavailable";
                return TextReplacementResult.ClipboardUnavailable;
            }
            selectedByUs = true;
            if (!SelectRangeAndConfirm(element, pattern, range, NormalizeNewlines(original), canCommit, true,
                    out diagnostic))
            {
                if (canCommit is not null && !canCommit())
                {
                    diagnostic = "stage=manual-selection-stale mutationAttempted=False";
                    return TextReplacementResult.Stale;
                }
                if (TryReplaceWholeValue(element, original, replacement, canCommit))
                {
                    applied = true;
                    diagnostic += " fallback=whole-value-applied";
                    return TextReplacementResult.Applied;
                }
                if (canCommit is not null && !canCommit())
                {
                    diagnostic = "stage=manual-whole-value-stale mutationAttempted=False";
                    return TextReplacementResult.Stale;
                }
                return TextReplacementResult.SelectionUnavailable;
            }
            if (canCommit is not null && !canCommit())
            {
                diagnostic = "stage=manual-pre-first-mutation-stale mutationAttempted=False";
                return TextReplacementResult.Stale;
            }
            var handle = GetFocusedHandle(element);
            var shortcutFirst = !IsNativeEditHandle(handle);
            mutationAttempted = true;
            var firstPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (firstPaste && VerifyInsertedText(pattern, NormalizeNewlines(replacement)))
            {
                applied = true;
                return TextReplacementResult.Applied;
            }
            if (!SelectionMatches(pattern, NormalizeNewlines(original)))
            {
                diagnostic = "stage=first-paste-selection-lost mutation-ambiguous";
                return TextReplacementResult.PasteFailed;
            }
            if (canCommit is not null && !canCommit())
            {
                diagnostic = "stage=after-first-mutation-currentness-lost mutation-ambiguous";
                return TextReplacementResult.PasteFailed;
            }
            // Some controls occasionally acknowledge WM_PASTE without consuming it.
            // Repeating the same method is safe only while the exact original range
            // remains selected.
            Thread.Sleep(15);
            if (canCommit is not null && !canCommit())
            {
                diagnostic = "stage=before-retry-mutation-currentness-lost mutation-ambiguous";
                return TextReplacementResult.PasteFailed;
            }
            var retryPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (retryPaste && VerifyInsertedText(pattern, NormalizeNewlines(replacement)))
            {
                applied = true;
                return TextReplacementResult.Applied;
            }
            if (!SelectionMatches(pattern, NormalizeNewlines(original)))
            {
                diagnostic = "stage=retry-paste-selection-lost mutation-ambiguous";
                return TextReplacementResult.PasteFailed;
            }
            if (canCommit is not null && !canCommit())
            {
                diagnostic = "stage=after-retry-mutation-currentness-lost mutation-ambiguous";
                return TextReplacementResult.PasteFailed;
            }
            var fallbackPaste = shortcutFirst ? handle != IntPtr.Zero && SendPaste(handle) : SendPasteShortcut();
            if (fallbackPaste && VerifyInsertedText(pattern, NormalizeNewlines(replacement)))
            {
                applied = true;
                return TextReplacementResult.Applied;
            }
            diagnostic = "stage=fallback-paste-failed original-selection-confirmed=" +
                         SelectionMatches(pattern, NormalizeNewlines(original));
            return TextReplacementResult.PasteFailed;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            diagnostic = $"stage=exception type={ex.GetType().Name} hresult={ex.HResult} " +
                         $"mutationAttempted={mutationAttempted}";
            return TextReplacementResult.EditorUnavailable;
        }
        finally
        {
            if (!applied && selectedByUs && pattern is not null && originalSelection is not null)
                RestoreSelectionAfterFailure(pattern, NormalizeNewlines(original), originalSelection);
            if (clipboard is not null) TryRestoreClipboard(clipboard, NormalizeNewlines(replacement));
            Interlocked.Exchange(ref _isInjecting, 0);
        }
    }

    internal static bool ReplaceForElement(AutomationElement element, string original, string replacement,
        Func<bool>? canCommit = null, Action<string>? reportFailure = null)
    {
        if (element.Current.IsPassword)
        {
            reportFailure?.Invoke("stage=element password");
            return false;
        }
        // Prefer TextPattern whenever the control exposes it. It verifies the exact
        // range at the caret and preserves the caret position. ValuePattern.SetValue
        // commonly resets modern Notepad to the beginning of the document.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out _))
        {
            var valueFallbackAllowed = HasSingleCaret(element);
            string? textDiagnostic = null;
            if (TryReplaceTextRange(element, original, replacement, canCommit,
                    detail => textDiagnostic = detail)) return true;
            // Chromium and WebView editors can expose TextPattern while temporarily
            // refusing range selection. ValuePattern is a safe fallback only when
            // the complete value still ends with the exact expected suffix and there
            // was no active selection when replacement started.
            if (!valueFallbackAllowed)
            {
                reportFailure?.Invoke($"text=[{textDiagnostic ?? "stage=text-range-unavailable"}] " +
                                      "fallback=[stage=value-fallback active-selection]");
                return false;
            }
            string? valueDiagnostic = null;
            var applied = TryReplaceValue(element, original, replacement, canCommit,
                detail => valueDiagnostic = detail);
            if (!applied)
                reportFailure?.Invoke($"text=[{textDiagnostic ?? "stage=text-range-unavailable"}] " +
                                      $"fallback=[{valueDiagnostic ?? "stage=value-range-unavailable"}]");
            return applied;
        }
        return TryReplaceValue(element, original, replacement, canCommit, reportFailure);
    }

    private static bool HasSingleCaret(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) || raw is not TextPattern pattern) return false;
        var selections = pattern.GetSelection();
        return selections.Length == 1 && selections[0].CompareEndpoints(TextPatternRangeEndpoint.Start,
            selections[0], TextPatternRangeEndpoint.End) == 0;
    }

    private static bool TryReplaceValue(AutomationElement element, string original, string replacement,
        Func<bool>? canCommit, Action<string>? reportFailure = null)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) || raw is not ValuePattern pattern ||
            pattern.Current.IsReadOnly)
        {
            reportFailure?.Invoke("stage=value-pattern unavailable-or-readonly");
            return false;
        }
        var value = pattern.Current.Value;
        var nativeOriginal = MatchNativeNewlines(value, original);
        if (!value.EndsWith(nativeOriginal, StringComparison.Ordinal))
        {
            var actualSuffix = value.Length <= nativeOriginal.Length ? value : value[^nativeOriginal.Length..];
            if (IsLeadingCaseOnlyDifference(nativeOriginal, actualSuffix))
            {
                // Some editors capitalize the first character without raising an input
                // event. Reconcile only that one harmless difference; every remaining
                // character must still match exactly before we touch the control.
                nativeOriginal = actualSuffix;
            }
            else
            {
                reportFailure?.Invoke($"stage=value-suffix-mismatch valueLength={value.Length} " +
                                      $"targetLength={nativeOriginal.Length} " + DescribeMismatch(nativeOriginal, actualSuffix));
                return false;
            }
        }
        var nativeReplacement = MatchNativeNewlines(value, replacement);
        var updated = value[..^nativeOriginal.Length] + nativeReplacement;
        if (canCommit is not null && !canCommit())
        {
            reportFailure?.Invoke("stage=value-pre-commit stale");
            return false;
        }
        pattern.SetValue(updated);
        if (!string.Equals(pattern.Current.Value, updated, StringComparison.Ordinal))
        {
            reportFailure?.Invoke("stage=value-verification mismatch");
            return false;
        }
        TryMoveValueCaretToEnd(element);
        return true;
    }

    private static bool TryReplaceTextRange(AutomationElement element, string original, string replacement,
        Func<bool>? canCommit, Action<string>? reportFailure)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) || raw is not TextPattern pattern)
        {
            reportFailure?.Invoke("stage=text-pattern-unavailable");
            return false;
        }
        var selections = pattern.GetSelection();
        if (selections.Length != 1)
        {
            reportFailure?.Invoke($"stage=text-selection-count count={selections.Length}");
            return false;
        }
        var caret = selections[0];
        if (caret.CompareEndpoints(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End) != 0)
        {
            reportFailure?.Invoke("stage=text-selection-active");
            return false;
        }
        var originalCaret = caret.Clone();
        var range = caret.Clone();
        var normalizedOriginal = NormalizeNewlines(original);
        var normalizedReplacement = NormalizeNewlines(replacement);
        var fullOriginal = normalizedOriginal;
        var fullReplacement = normalizedReplacement;
        if (normalizedOriginal.Length > 0)
        {
            var moved = range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -normalizedOriginal.Length);
            if (moved != -normalizedOriginal.Length)
            {
                reportFailure?.Invoke($"stage=text-range-short moved={moved} expected={-normalizedOriginal.Length}");
                return false;
            }
            var rangeText = NormalizeNewlines(range.GetText(-1));
            if (!string.Equals(rangeText, normalizedOriginal, StringComparison.Ordinal))
            {
                if (IsLeadingCaseOnlyDifference(normalizedOriginal, rangeText))
                {
                    normalizedOriginal = rangeText;
                    fullOriginal = rangeText;
                }
                else
                {
                    reportFailure?.Invoke($"stage=text-suffix-mismatch actualLength={rangeText.Length} " +
                                          $"targetLength={normalizedOriginal.Length} " +
                                          DescribeMismatch(normalizedOriginal, rangeText));
                    return false;
                }
            }
        }

        // Keep an Enter-generated paragraph marker in place when both strings end
        // with it. RichEdit controls do not reliably preserve a final newline when
        // that marker is selected and replaced, which previously moved the corrected
        // sentence onto the wrong line.
        var preservedNewlines = CommonTrailingNewlineCount(normalizedOriginal, normalizedReplacement);
        if (preservedNewlines > 0)
        {
            var moved = range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -preservedNewlines);
            if (moved != -preservedNewlines)
            {
                reportFailure?.Invoke("stage=text-newline-range-short");
                return false;
            }
            normalizedOriginal = normalizedOriginal[..^preservedNewlines];
            normalizedReplacement = normalizedReplacement[..^preservedNewlines];
            if (!string.Equals(NormalizeNewlines(range.GetText(-1)), normalizedOriginal, StringComparison.Ordinal))
            {
                reportFailure?.Invoke("stage=text-newline-suffix-mismatch");
                return false;
            }
        }
        var valueVerification = CreateSuffixValueVerification(element, fullOriginal, fullReplacement,
            normalizedReplacement);
        if (canCommit is not null && !canCommit())
        {
            reportFailure?.Invoke("stage=text-pre-commit-stale");
            return false;
        }
        var handle = GetFocusedHandle(element);

        if (normalizedReplacement.Length == 0)
        {
            if (canCommit is not null && !canCommit()) return false;
            if (!SelectRangeAndConfirm(element, pattern, range, normalizedOriginal, canCommit, false,
                    out var selectionDiagnostic))
            {
                reportFailure?.Invoke(selectionDiagnostic);
                RestoreSelectionAfterFailure(pattern, normalizedOriginal, originalCaret);
                return false;
            }
            var edited = normalizedOriginal.Length == 0 || SendClear(element);
            if (!edited)
            {
                RestoreSelectionAfterFailure(pattern, normalizedOriginal, originalCaret);
                return false;
            }
            RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);
            return true;
        }

        ClipboardSnapshot? clipboard = null;
        var selectedByUs = false;
        var mutationApplied = false;
        try
        {
            if (!TryPrepareClipboard(normalizedReplacement, out clipboard))
            {
                reportFailure?.Invoke("stage=clipboard-unavailable");
                return false;
            }
            if (canCommit is not null && !canCommit())
            {
                reportFailure?.Invoke("stage=text-pre-selection-stale");
                return false;
            }
            selectedByUs = true;
            // Some Chromium/WebView providers accept Range.Select without actually
            // selecting the requested text. Pasting in that state appends the
            // correction, producing "originaloriginal corrected". Never paste
            // until UI Automation confirms the exact range is selected.
            if (!SelectRangeAndConfirm(element, pattern, range, normalizedOriginal, canCommit, false,
                    out var selectionDiagnostic))
            {
                reportFailure?.Invoke(selectionDiagnostic);
                return false;
            }
            if (canCommit is not null && !canCommit())
            {
                reportFailure?.Invoke("stage=text-pre-first-mutation-stale mutationAttempted=False");
                return false;
            }
            var shortcutFirst = !IsNativeEditHandle(handle);
            var firstPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (firstPaste && VerifyInsertedText(pattern, normalizedReplacement, valueVerification))
            {
                mutationApplied = true;
                RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);
                return true;
            }

            // Chromium address bars expose a TextPattern range but ignore WM_PASTE.
            // Only fall back to a real paste chord if the original selection is still
            // intact, so a partial edit can never be duplicated.
            if (!SelectionMatches(pattern, normalizedOriginal))
            {
                reportFailure?.Invoke("stage=first-paste-selection-lost mutation-ambiguous");
                return false;
            }
            if (canCommit is not null && !canCommit())
            {
                reportFailure?.Invoke("stage=after-first-mutation-currentness-lost mutation-ambiguous");
                return false;
            }
            Thread.Sleep(15);
            if (canCommit is not null && !canCommit())
            {
                reportFailure?.Invoke("stage=before-retry-mutation-currentness-lost mutation-ambiguous");
                return false;
            }
            var retryPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (retryPaste && VerifyInsertedText(pattern, normalizedReplacement, valueVerification))
            {
                mutationApplied = true;
                RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);
                return true;
            }
            if (!SelectionMatches(pattern, normalizedOriginal))
            {
                reportFailure?.Invoke("stage=retry-paste-selection-lost mutation-ambiguous");
                return false;
            }
            if (canCommit is not null && !canCommit())
            {
                reportFailure?.Invoke("stage=after-retry-mutation-currentness-lost mutation-ambiguous");
                return false;
            }
            var fallbackPaste = shortcutFirst ? handle != IntPtr.Zero && SendPaste(handle) : SendPasteShortcut();
            if (!fallbackPaste || !VerifyInsertedText(pattern, normalizedReplacement, valueVerification))
            {
                reportFailure?.Invoke("stage=fallback-paste verification-failed original-selection-confirmed=" +
                                      SelectionMatches(pattern, normalizedOriginal));
                return false;
            }
            mutationApplied = true;
            RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);
            return true;
        }
        finally
        {
            if (!mutationApplied && selectedByUs)
                RestoreSelectionAfterFailure(pattern, normalizedOriginal, originalCaret);
            if (clipboard is not null) TryRestoreClipboard(clipboard, normalizedReplacement);
        }
    }

    private static bool IsNativeEditHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;
        var className = new System.Text.StringBuilder(128);
        return NativeMethods.GetClassName(handle, className, className.Capacity) > 0 &&
               className.ToString().Contains("EDIT", StringComparison.OrdinalIgnoreCase);
    }

    private static int CommonTrailingNewlineCount(string left, string right)
    {
        var count = 0;
        while (count < left.Length && count < right.Length &&
               left[^(count + 1)] == '\n' && right[^(count + 1)] == '\n') count++;
        return count;
    }

    private static IntPtr GetFocusedHandle(AutomationElement element)
    {
        var handle = new IntPtr(element.Current.NativeWindowHandle);
        if (handle != IntPtr.Zero) return handle;
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        return NativeMethods.GetGUIThreadInfo(0, ref info) ? info.Focus : IntPtr.Zero;
    }

    private static bool SelectionMatches(TextPattern pattern, string expected)
    {
        var selections = pattern.GetSelection();
        return selections.Length == 1 &&
               string.Equals(NormalizeNewlines(selections[0].GetText(-1)), expected, StringComparison.Ordinal);
    }

    private static bool SelectRangeAndConfirm(AutomationElement element, TextPattern pattern, TextPatternRange range,
        string expected, Func<bool>? canCommit, bool restoreFocus, out string diagnostic)
    {
        var attempts = 0;
        for (; attempts < SelectionAttempts; attempts++)
        {
            if (canCommit is not null && !canCommit())
            {
                diagnostic = $"stage=selection stale attempts={attempts}";
                return false;
            }
            if (restoreFocus) element.SetFocus();
            range.Select();
            var deadline = Environment.TickCount64 + SelectionAttemptTimeoutMs;
            do
            {
                if (SelectionMatches(pattern, expected))
                {
                    diagnostic = $"stage=selection confirmed attempts={attempts + 1}";
                    return true;
                }
                if (canCommit is not null && !canCommit())
                {
                    diagnostic = $"stage=selection stale attempts={attempts + 1}";
                    return false;
                }
                if (Environment.TickCount64 >= deadline) break;
                Thread.Sleep(10);
            } while (true);
            Thread.Sleep(10);
        }
        diagnostic = $"stage=selection timeout attempts={attempts} {DescribeSelectionState(element, pattern)}";
        return false;
    }

    private static string DescribeSelectionState(AutomationElement element, TextPattern pattern)
    {
        try
        {
            var selections = pattern.GetSelection();
            var selectedLength = selections.Length == 1 ? NormalizeNewlines(selections[0].GetText(-1)).Length : -1;
            var degenerate = selections.Length == 1 && selections[0].CompareEndpoints(
                TextPatternRangeEndpoint.Start, selections[0], TextPatternRangeEndpoint.End) == 0;
            var focused = AutomationElement.FocusedElement;
            var focusMatch = focused is not null && Automation.Compare(focused, element);
            var className = element.Current.ClassName ?? string.Empty;
            return $"selectionCount={selections.Length} selectedLength={selectedLength} degenerate={degenerate} " +
                   $"focusMatch={focusMatch} class={className} nativeHandle={element.Current.NativeWindowHandle}";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return $"selectionStateError={ex.GetType().Name} hresult={ex.HResult}";
        }
    }

    internal static bool TryReplaceWholeValue(AutomationElement element, string original, string replacement,
        Func<bool>? canCommit = null)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) || raw is not ValuePattern pattern ||
            pattern.Current.IsReadOnly) return false;
        var current = pattern.Current.Value;
        var nativeOriginal = MatchNativeNewlines(current, original);
        if (!string.Equals(current, nativeOriginal, StringComparison.Ordinal)) return false;
        var nativeReplacement = MatchNativeNewlines(current, replacement);
        if (canCommit is not null && !canCommit()) return false;
        pattern.SetValue(nativeReplacement);
        if (!string.Equals(pattern.Current.Value, nativeReplacement, StringComparison.Ordinal)) return false;
        TryMoveValueCaretToEnd(element);
        // Win over a delayed Range.Select request from a Chromium/WebView provider.
        Thread.Sleep(75);
        TryMoveValueCaretToEnd(element);
        return true;
    }

    internal static void RestoreSelectionAfterFailure(TextPattern pattern, string selectedText,
        TextPatternRange originalSelection)
    {
        try
        {
            // Chromium/WebView can apply Range.Select after returning from the call.
            // Watch briefly and undo only our exact temporary selection. Reissuing
            // the original range at the end also wins over a delayed provider update.
            var deadline = Environment.TickCount64 + 180;
            do
            {
                var selections = pattern.GetSelection();
                if (SelectionMatches(pattern, selectedText)) originalSelection.Select();
                else if (selections.Length != 1 || !RangesEqual(selections[0], originalSelection)) return;
                if (Environment.TickCount64 >= deadline) break;
                Thread.Sleep(10);
            } while (true);
            originalSelection.Select();
            Thread.Sleep(20);
            if (SelectionMatches(pattern, selectedText) && !RangesEqual(pattern.GetSelection()[0], originalSelection))
                originalSelection.Select();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) { }
    }

    private static bool RangesEqual(TextPatternRange left, TextPatternRange right) =>
        left.CompareEndpoints(TextPatternRangeEndpoint.Start, right, TextPatternRangeEndpoint.Start) == 0 &&
        left.CompareEndpoints(TextPatternRangeEndpoint.End, right, TextPatternRangeEndpoint.End) == 0;

    private static ValueVerification? CreateSuffixValueVerification(AutomationElement element, string original,
        string replacement, string pastedReplacement)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) || raw is not ValuePattern valuePattern ||
            valuePattern.Current.IsReadOnly) return null;
        var before = valuePattern.Current.Value;
        var normalizedBefore = NormalizeNewlines(before);
        if (!normalizedBefore.EndsWith(original, StringComparison.Ordinal)) return null;
        var expected = normalizedBefore[..^original.Length] + replacement;
        return new ValueVerification(element, valuePattern, MatchNativeNewlines(before, expected),
            MatchNativeNewlines(before, normalizedBefore + pastedReplacement));
    }

    private static bool VerifyInsertedText(TextPattern pattern, string expected, ValueVerification? valueVerification = null)
    {
        if (expected.Length == 0) return true;
        var deadline = Environment.TickCount64 + 250;
        do
        {
            if (valueVerification is not null)
            {
                var current = valueVerification.Pattern.Current.Value;
                if (string.Equals(current, valueVerification.Expected, StringComparison.Ordinal)) return true;
                // A few WebView editors report the range as selected but still
                // append on paste. Repair only the exact, unambiguous duplicate;
                // any concurrent user edit makes this comparison fail.
                if (string.Equals(current, valueVerification.Duplicated, StringComparison.Ordinal))
                {
                    valueVerification.Pattern.SetValue(valueVerification.Expected);
                    if (string.Equals(valueVerification.Pattern.Current.Value, valueVerification.Expected,
                            StringComparison.Ordinal))
                    {
                        TryMoveValueCaretToEnd(valueVerification.Element);
                        return true;
                    }
                }
            }
            var selections = pattern.GetSelection();
            if (selections.Length == 1)
            {
                var caret = selections[0];
                if (caret.CompareEndpoints(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End) == 0)
                {
                    var inserted = caret.Clone();
                    var moved = inserted.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -expected.Length);
                    if (moved == -expected.Length &&
                        string.Equals(NormalizeNewlines(inserted.GetText(-1)), expected, StringComparison.Ordinal) &&
                        valueVerification is null) return true;
                }
            }
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(10);
        } while (true);
    }

    private sealed record ValueVerification(AutomationElement Element, ValuePattern Pattern, string Expected,
        string Duplicated);

    private static bool RestoreCaretAfterPreservedNewlines(TextPattern pattern, int newlineCount)
    {
        if (newlineCount == 0) return true;
        var selections = pattern.GetSelection();
        if (selections.Length != 1) return false;
        var caret = selections[0];
        if (caret.CompareEndpoints(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End) != 0) return false;
        var restored = caret.Clone();
        if (restored.Move(TextUnit.Character, newlineCount) != newlineCount) return false;
        restored.Select();
        return true;
    }

    internal static bool TryMoveValueCaretToEnd(AutomationElement element)
    {
        try
        {
            element.SetFocus();
            var deadline = Environment.TickCount64 + 250;
            while (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) && raw is TextPattern pattern)
            {
                var document = pattern.DocumentRange;
                var end = document.Clone();
                end.MoveEndpointByRange(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.End);
                end.Select();
                var selections = pattern.GetSelection();
                if (selections.Length == 1 &&
                    selections[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selections[0],
                        TextPatternRangeEndpoint.End) == 0 &&
                    selections[0].CompareEndpoints(TextPatternRangeEndpoint.End, document,
                        TextPatternRangeEndpoint.End) == 0) return true;
                if (Environment.TickCount64 >= deadline) break;
                Thread.Sleep(10);
            }
            return SendInputBatch(
            [
                Create((ushort)NativeMethods.VkControl, 0, 0),
                Create((ushort)NativeMethods.VkEnd, 0, 0),
                Create((ushort)NativeMethods.VkEnd, 0, NativeMethods.KeyeventfKeyup),
                Create((ushort)NativeMethods.VkControl, 0, NativeMethods.KeyeventfKeyup)
            ]);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return false;
        }
    }

    private static bool SendClear(AutomationElement element)
    {
        var handle = GetFocusedHandle(element);
        return handle != IntPtr.Zero && SendMessage(handle, NativeMethods.WmClear, UIntPtr.Zero);
    }

    private static string MatchNativeNewlines(string existingValue, string value)
    {
        var normalized = NormalizeNewlines(value);
        if (existingValue.Contains("\r\n", StringComparison.Ordinal))
            return normalized.Replace("\n", "\r\n", StringComparison.Ordinal);
        if (existingValue.Contains('\r')) return normalized.Replace('\n', '\r');
        return normalized;
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static string DescribeMismatch(string expected, string actual)
    {
        var prefix = 0;
        while (prefix < expected.Length && prefix < actual.Length && expected[prefix] == actual[prefix]) prefix++;
        var suffix = 0;
        while (suffix < expected.Length - prefix && suffix < actual.Length - prefix &&
               expected[^(suffix + 1)] == actual[^(suffix + 1)]) suffix++;
        var expectedKind = prefix < expected.Length ? CharacterKind(expected[prefix]) : "end-of-text";
        var actualKind = prefix < actual.Length ? CharacterKind(actual[prefix]) : "end-of-text";
        var caseEquivalent = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            .ToString().ToLowerInvariant();
        return $"mismatchIndex={prefix} commonSuffix={suffix} expectedKind={expectedKind} actualKind={actualKind} " +
               $"caseEquivalent={caseEquivalent}";
    }

    internal static bool IsLeadingCaseOnlyDifference(string expected, string actual)
    {
        if (expected.Length == 0 || expected.Length != actual.Length || expected[0] == actual[0]) return false;
        if (!expected.AsSpan(1).SequenceEqual(actual.AsSpan(1))) return false;

        var expectedFirst = expected[0];
        var actualFirst = actual[0];
        if (!char.IsLetter(expectedFirst) || !char.IsLetter(actualFirst)) return false;
        var oppositeCase = char.IsUpper(expectedFirst) && char.IsLower(actualFirst) ||
                           char.IsLower(expectedFirst) && char.IsUpper(actualFirst);
        return oppositeCase && char.ToUpperInvariant(expectedFirst) == char.ToUpperInvariant(actualFirst) &&
               char.ToLowerInvariant(expectedFirst) == char.ToLowerInvariant(actualFirst);
    }

    private static string CharacterKind(char value)
    {
        if (value == ' ') return "space";
        if (value == '\u00A0') return "nonbreaking-space";
        if (value is '\r' or '\n') return "line-break";
        if (char.IsWhiteSpace(value)) return "other-whitespace";
        return CharUnicodeInfo.GetUnicodeCategory(value) switch
        {
            UnicodeCategory.Format => "format-marker",
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
                UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
                UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
                UnicodeCategory.OtherPunctuation => "punctuation",
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => "number",
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => "letter",
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark =>
                "combining-mark",
            _ => "other"
        };
    }

    internal static IReadOnlyList<uint> BuildMessagePlan(int characterCount, bool includePaste)
    {
        var plan = Enumerable.Repeat((uint)NativeMethods.WmChar, characterCount).ToList();
        if (includePaste) plan.Add(NativeMethods.WmPaste);
        return plan;
    }

    internal static NativeMethods.Input[] BuildDeletionInputs(int characterCount)
    {
        var inputs = new List<NativeMethods.Input>(characterCount * 2);
        for (var i = 0; i < characterCount; i++) AddVirtualKey(inputs, NativeMethods.VkBack);
        return inputs.ToArray();
    }

    private static void AddVirtualKey(List<NativeMethods.Input> inputs, int key)
    {
        inputs.Add(Create((ushort)key, 0, 0));
        inputs.Add(Create((ushort)key, 0, NativeMethods.KeyeventfKeyup));
    }

    private static bool SendPasteShortcut()
    {
        return SendInputBatch(
        [
            Create((ushort)NativeMethods.VkControl, 0, 0),
            Create((ushort)NativeMethods.VkV, 0, 0),
            Create((ushort)NativeMethods.VkV, 0, NativeMethods.KeyeventfKeyup),
            Create((ushort)NativeMethods.VkControl, 0, NativeMethods.KeyeventfKeyup)
        ]);
    }

    internal static bool SendVirtualKeyPair(int virtualKey) => SendInputBatch(
    [
        Create((ushort)virtualKey, 0, 0),
        Create((ushort)virtualKey, 0, NativeMethods.KeyeventfKeyup)
    ]);

    private static bool SendSingle(NativeMethods.Input input)
    {
        unsafe { return NativeMethods.SendInput(1, &input, Marshal.SizeOf<NativeMethods.Input>()) == 1; }
    }

    private static bool SendInputBatch(NativeMethods.Input[] inputs)
    {
        if (inputs.Length == 0) return true;
        unsafe
        {
            fixed (NativeMethods.Input* pointer = inputs)
                return NativeMethods.SendInput((uint)inputs.Length, pointer, Marshal.SizeOf<NativeMethods.Input>()) == inputs.Length;
        }
    }

    internal static bool SendEditSequence(IntPtr focusedWindow, int characterCount) =>
        SendBackspaces(focusedWindow, characterCount) && SendPaste(focusedWindow);

    internal static bool SendBackspaces(IntPtr focusedWindow, int characterCount)
    {
        for (var i = 0; i < characterCount; i++)
            if (!SendMessage(focusedWindow, NativeMethods.WmChar, new UIntPtr(NativeMethods.VkBack))) return false;
        return true;
    }

    internal static bool SendEnter(IntPtr focusedWindow) =>
        SendMessage(focusedWindow, NativeMethods.WmChar, new UIntPtr('\r'));

    private static bool SendPaste(IntPtr focusedWindow) =>
        SendMessage(focusedWindow, NativeMethods.WmPaste, UIntPtr.Zero);

    private static bool SendMessage(IntPtr window, uint message, UIntPtr value) =>
        NativeMethods.SendMessageTimeout(window, message, value, IntPtr.Zero, 0x0002, 250, out _) != IntPtr.Zero;

    private static bool TryPrepareClipboard(string text, out ClipboardSnapshot? snapshot)
    {
        snapshot = null;
        System.Windows.IDataObject? original = null;
        var capturedOriginal = false;
        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            ClipboardSnapshot? prepared = null;
            try
            {
                var result = InvokeClipboard(() =>
                {
                    if (!capturedOriginal)
                    {
                        original = CloneClipboardData(System.Windows.Clipboard.GetDataObject());
                        capturedOriginal = true;
                    }
                    var data = new System.Windows.DataObject();
                    data.SetData(System.Windows.DataFormats.UnicodeText, text);
                    data.SetData(ExcludeClipboardFormat, new byte[] { 0 });
                    // Materialize the complete value now. Delayed rendering can expose only the
                    // first character when the clipboard owner is restored shortly after paste.
                    System.Windows.Clipboard.SetDataObject(data, true);
                    if (!System.Windows.Clipboard.ContainsText() ||
                        !string.Equals(System.Windows.Clipboard.GetText(), text, StringComparison.Ordinal))
                        return false;
                    prepared = new ClipboardSnapshot(original);
                    return true;
                });
                if (result)
                {
                    snapshot = prepared;
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException or InvalidComObjectException) { }
            if (attempt + 1 < ClipboardAttempts) Thread.Sleep(ClipboardRetryDelayMs);
        }
        if (capturedOriginal) TryRestoreClipboard(new ClipboardSnapshot(original), text);
        return false;
    }

    private static void TryRestoreClipboard(ClipboardSnapshot snapshot, string injectedText)
    {
        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            try
            {
                InvokeClipboard(() =>
                {
                    // Do not overwrite a clipboard change made by the user during injection.
                    if (!System.Windows.Clipboard.ContainsText() ||
                        !string.Equals(System.Windows.Clipboard.GetText(), injectedText, StringComparison.Ordinal)) return true;
                    if (snapshot.Previous is null) System.Windows.Clipboard.Clear();
                    else System.Windows.Clipboard.SetDataObject(snapshot.Previous, true);
                    return true;
                });
                return;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException or InvalidComObjectException) { }
            if (attempt + 1 < ClipboardAttempts) Thread.Sleep(ClipboardRetryDelayMs);
        }
    }

    private static System.Windows.IDataObject? CloneClipboardData(System.Windows.IDataObject? source)
    {
        if (source is null) return null;
        var clone = new System.Windows.DataObject();
        var copied = false;
        string[] formats;
        try { formats = source.GetFormats(false); }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidComObjectException) { return null; }
        foreach (var format in formats.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var data = source.GetData(format, false);
                data = data switch
                {
                    MemoryStream stream => new MemoryStream(stream.ToArray()),
                    byte[] bytes => bytes.ToArray(),
                    string[] strings => strings.ToArray(),
                    System.Windows.Media.Imaging.BitmapSource bitmap => bitmap.Clone(),
                    System.Drawing.Image image => image.Clone(),
                    string text => text,
                    _ => null
                };
                if (data is null) continue;
                clone.SetData(format, data);
                copied = true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException or InvalidComObjectException or NotSupportedException) { }
        }
        return copied ? clone : null;
    }

    private static T InvokeClipboard<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null) return dispatcher.Invoke(action);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Clipboard access requires an STA thread.");
        return action();
    }

    private sealed record ClipboardSnapshot(System.Windows.IDataObject? Previous);

    private static NativeMethods.Input Create(ushort key, ushort scan, uint flags) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Keyboard = new NativeMethods.KeybdInput
        {
            VirtualKey = key, ScanCode = scan, Flags = flags, ExtraInfo = NativeMethods.InjectionMarker
        }
    };
}
