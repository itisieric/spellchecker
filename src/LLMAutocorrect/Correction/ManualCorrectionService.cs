using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Memory;
using LLMAutocorrect.Models;
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
    private readonly SemaphoreSlim _correctionGate = new(1, 1);

    public ManualCorrectionService(ICorrectionProvider provider, SettingsManager settings, TechnicalDictionary dictionary,
        ProtectedTokenDetector tokens, CorrectionValidator validator, ApplicationExclusionManager exclusions,
        ForegroundWindowService foreground, FocusedControlService focused, SendInputService input,
        WritingMemory memory, SpellingHistory spellingHistory,
        ManualCorrectionOverlay offer, StatusOverlay status, DiagnosticsLogger logger)
    {
        _provider = provider; _settings = settings; _dictionary = dictionary; _tokens = tokens; _validator = validator;
        _exclusions = exclusions; _foreground = foreground; _focused = focused; _input = input; _memory = memory;
        _spellingHistory = spellingHistory; _offer = offer; _status = status; _logger = logger;
    }

    public void OfferAt(System.Windows.Point point)
    {
        var settings = _settings.Current;
        if (!settings.Enabled || settings.PrivateMode || !settings.ManualRightClickCorrectionEnabled) return;
        var window = _foreground.GetCurrent();
        if (!_exclusions.IsAutocorrectAllowed(window.ProcessName)) return;
        if (!TryCapture(window, point, out var target)) return;
        _offer.Offer(target.OriginalText, point, () => CorrectAsync(target));
    }

    public void OfferCurrent()
    {
        var settings = _settings.Current;
        if (!settings.Enabled || settings.PrivateMode) return;
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

    private async Task CorrectAsync(ManualCorrectionTarget target)
    {
        if (!await _correctionGate.WaitAsync(0)) return;
        try
        {
            if (!_foreground.Matches(target.Window.WindowHandle, target.Window.ProcessId)) return;
            var settings = _settings.Current;
            var preserveAddressBarStyle = ShouldPreserveAddressBarStyle(settings, _focused.GetCurrent(), target.Window.ProcessName);
            var protectedTokens = _tokens.Detect(target.OriginalText, _dictionary.Terms);
            var request = new CorrectionRequest(string.Empty, target.OriginalText, _dictionary.Terms, protectedTokens,
                CorrectionMode.Conservative, settings.CustomCorrectionInstructions, preserveAddressBarStyle,
                _memory.GetSpellingHints(target.OriginalText));
            CorrectionResult result;
            try { result = await _provider.CorrectAsync(request, CancellationToken.None); }
            catch (Exception ex)
            {
                await _logger.WriteAsync("ManualCorrectionFailed", new Dictionary<string, object?> { ["Type"] = ex.GetType().Name });
                ShowStatus(target, "Spelling correction is temporarily unavailable.");
                return;
            }
            if (!result.ShouldReplace)
            {
                ShowStatus(target, result.Uncertain
                    ? "Not corrected: " + CleanReason(result.Reason)
                    : "No spelling correction was needed.");
                return;
            }
            var validation = _validator.Validate(request, result, Math.Max(.60, settings.MaximumEditRatio));
            if (!validation.IsValid)
            {
                await _logger.WriteAsync("ManualCorrectionRejected", new Dictionary<string, object?>
                {
                    ["Process"] = target.Window.ProcessName,
                    ["TargetLength"] = target.OriginalText.Length,
                    ["EditRatio"] = validation.EditRatio.ToString("F3"),
                    ["Validation"] = validation.Reason,
                    ["Style"] = preserveAddressBarStyle ? "address-bar-spelling" : "standard"
                });
                ShowStatus(target, ValidationMessage(validation.Reason));
                return;
            }
            var replacement = result.Replacement.Trim();
            var applyTarget = RefreshTarget(target);
            var replacementResult = _input.ReplaceTextRangeDetailed(applyTarget.Element, applyTarget.Range,
                applyTarget.OriginalText, replacement, out var replacementDiagnostic);
            if (replacementResult != TextReplacementResult.Applied)
            {
                await _logger.WriteAsync("ManualCorrectionApplyFailed", new Dictionary<string, object?>
                {
                    ["Process"] = target.Window.ProcessName,
                    ["Result"] = replacementResult.ToString(),
                    ["TargetLength"] = target.OriginalText.Length,
                    ["Diagnostic"] = replacementDiagnostic
                });
                ShowStatus(target, ReplacementFailureMessage(replacementResult, replacementDiagnostic));
                return;
            }
            await _memory.RecordCorrectionAsync(target.OriginalText, replacement, protectedTokens);
            await _spellingHistory.RecordCorrectionAsync(target.OriginalText, replacement, protectedTokens);
            ShowStatus(target, $"{target.OriginalText} → {replacement}");
            await _logger.WriteAsync("ManualCorrectionCompleted", new Dictionary<string, object?>
            {
                ["Characters"] = target.OriginalText.Length, ["EditRatio"] = validation.EditRatio.ToString("F3")
            });
        }
        finally { _correctionGate.Release(); }
    }

    private void ShowStatus(ManualCorrectionTarget target, string message)
    {
        var point = new CaretPositionService().GetCaretScreenPosition(target.Window.WindowHandle);
        _status.ShowMessage(message, point);
    }

    private static ManualCorrectionTarget RefreshTarget(ManualCorrectionTarget target)
    {
        if (target.CapturePoint is not { } point) return target;
        return TryCaptureFromElement(target.Element, target.Window, point, out var refreshed) &&
               string.Equals(refreshed.OriginalText, target.OriginalText, StringComparison.Ordinal)
            ? refreshed
            : target;
    }

    internal static string ReplacementFailureMessage(TextReplacementResult result, string? diagnostic = null) => result switch
    {
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
