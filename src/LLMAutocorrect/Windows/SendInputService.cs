using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using LLMAutocorrect.Input;

namespace LLMAutocorrect.Windows;

public sealed class SendInputService
{
    private const string ExcludeClipboardFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private readonly PhysicalKeyState _physicalKeys;
    private int _isInjecting;
    public bool IsInjecting => Volatile.Read(ref _isInjecting) != 0;

    public SendInputService(PhysicalKeyState? physicalKeys = null) => _physicalKeys = physicalKeys ?? new PhysicalKeyState();

    public bool WaitForPhysicalKeysReleased() => _physicalKeys.WaitUntilReleased(TimeSpan.FromMilliseconds(750));

    public bool ReplacePreviousText(string original, string replacement, Func<bool>? canCommit = null)
    {
        Interlocked.Exchange(ref _isInjecting, 1);
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword) return false;
            if (canCommit is not null && !canCommit()) return false;
            return ReplaceForElement(element, original, replacement, canCommit);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
        finally { Interlocked.Exchange(ref _isInjecting, 0); }
    }

    public bool InsertText(string text) => ReplacePreviousText(string.Empty, text);

    public bool ReplaceTextRange(AutomationElement element, TextPatternRange range, string original, string replacement)
    {
        Interlocked.Exchange(ref _isInjecting, 1);
        ClipboardSnapshot? clipboard = null;
        try
        {
            if (element.Current.IsPassword || !element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) ||
                raw is not TextPattern pattern ||
                !string.Equals(NormalizeNewlines(range.GetText(-1)), NormalizeNewlines(original), StringComparison.Ordinal)) return false;
            if (!TryPrepareClipboard(NormalizeNewlines(replacement), out clipboard)) return false;
            element.SetFocus();
            range.Select();
            if (!SelectionMatches(pattern, NormalizeNewlines(original))) return false;
            var handle = GetFocusedHandle(element);
            var shortcutFirst = !IsNativeEditHandle(handle);
            var firstPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (firstPaste && VerifyInsertedText(pattern, NormalizeNewlines(replacement))) return true;
            if (!SelectionMatches(pattern, NormalizeNewlines(original))) return false;
            // Some controls occasionally acknowledge WM_PASTE without consuming it.
            // Repeating the same method is safe only while the exact original range
            // remains selected.
            Thread.Sleep(15);
            var retryPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (retryPaste && VerifyInsertedText(pattern, NormalizeNewlines(replacement))) return true;
            if (!SelectionMatches(pattern, NormalizeNewlines(original))) return false;
            var fallbackPaste = shortcutFirst ? handle != IntPtr.Zero && SendPaste(handle) : SendPasteShortcut();
            return fallbackPaste && VerifyInsertedText(pattern, NormalizeNewlines(replacement));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
        finally
        {
            if (clipboard is not null) TryRestoreClipboard(clipboard, NormalizeNewlines(replacement));
            Interlocked.Exchange(ref _isInjecting, 0);
        }
    }

    internal static bool ReplaceForElement(AutomationElement element, string original, string replacement,
        Func<bool>? canCommit = null)
    {
        if (element.Current.IsPassword) return false;
        // Prefer TextPattern whenever the control exposes it. It verifies the exact
        // range at the caret and preserves the caret position. ValuePattern.SetValue
        // commonly resets modern Notepad to the beginning of the document.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out _))
        {
            var valueFallbackAllowed = HasSingleCaret(element);
            if (TryReplaceTextRange(element, original, replacement, canCommit)) return true;
            // Chromium and WebView editors can expose TextPattern while temporarily
            // refusing range selection. ValuePattern is a safe fallback only when
            // the complete value still ends with the exact expected suffix and there
            // was no active selection when replacement started.
            return valueFallbackAllowed && TryReplaceValue(element, original, replacement, canCommit);
        }
        return TryReplaceValue(element, original, replacement, canCommit);
    }

    private static bool HasSingleCaret(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) || raw is not TextPattern pattern) return false;
        var selections = pattern.GetSelection();
        return selections.Length == 1 && selections[0].CompareEndpoints(TextPatternRangeEndpoint.Start,
            selections[0], TextPatternRangeEndpoint.End) == 0;
    }

    private static bool TryReplaceValue(AutomationElement element, string original, string replacement,
        Func<bool>? canCommit)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) || raw is not ValuePattern pattern ||
            pattern.Current.IsReadOnly) return false;
        var value = pattern.Current.Value;
        var nativeOriginal = MatchNativeNewlines(value, original);
        if (!value.EndsWith(nativeOriginal, StringComparison.Ordinal)) return false;
        var nativeReplacement = MatchNativeNewlines(value, replacement);
        var updated = value[..^nativeOriginal.Length] + nativeReplacement;
        if (canCommit is not null && !canCommit()) return false;
        pattern.SetValue(updated);
        if (!string.Equals(pattern.Current.Value, updated, StringComparison.Ordinal)) return false;
        TryMoveValueCaretToEnd(element);
        return true;
    }

    private static bool TryReplaceTextRange(AutomationElement element, string original, string replacement,
        Func<bool>? canCommit)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) || raw is not TextPattern pattern) return false;
        var selections = pattern.GetSelection();
        if (selections.Length != 1) return false;
        var caret = selections[0];
        if (caret.CompareEndpoints(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End) != 0) return false;
        var range = caret.Clone();
        var normalizedOriginal = NormalizeNewlines(original);
        var normalizedReplacement = NormalizeNewlines(replacement);
        var fullOriginal = normalizedOriginal;
        var fullReplacement = normalizedReplacement;
        if (normalizedOriginal.Length > 0)
        {
            var moved = range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -normalizedOriginal.Length);
            if (moved != -normalizedOriginal.Length) return false;
            if (!string.Equals(NormalizeNewlines(range.GetText(-1)), normalizedOriginal, StringComparison.Ordinal)) return false;
        }

        // Keep an Enter-generated paragraph marker in place when both strings end
        // with it. RichEdit controls do not reliably preserve a final newline when
        // that marker is selected and replaced, which previously moved the corrected
        // sentence onto the wrong line.
        var preservedNewlines = CommonTrailingNewlineCount(normalizedOriginal, normalizedReplacement);
        if (preservedNewlines > 0)
        {
            var moved = range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -preservedNewlines);
            if (moved != -preservedNewlines) return false;
            normalizedOriginal = normalizedOriginal[..^preservedNewlines];
            normalizedReplacement = normalizedReplacement[..^preservedNewlines];
            if (!string.Equals(NormalizeNewlines(range.GetText(-1)), normalizedOriginal, StringComparison.Ordinal)) return false;
        }
        var valueVerification = CreateSuffixValueVerification(element, fullOriginal, fullReplacement,
            normalizedReplacement);
        if (canCommit is not null && !canCommit()) return false;
        var handle = GetFocusedHandle(element);

        if (normalizedReplacement.Length == 0)
        {
            if (canCommit is not null && !canCommit()) return false;
            range.Select();
            if (!SelectionMatches(pattern, normalizedOriginal) || canCommit is not null && !canCommit()) return false;
            var edited = normalizedOriginal.Length == 0 || SendClear(element);
            return edited && RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);
        }

        ClipboardSnapshot? clipboard = null;
        try
        {
            if (!TryPrepareClipboard(normalizedReplacement, out clipboard)) return false;
            if (canCommit is not null && !canCommit()) return false;
            range.Select();
            // Some Chromium/WebView providers accept Range.Select without actually
            // selecting the requested text. Pasting in that state appends the
            // correction, producing "originaloriginal corrected". Never paste
            // until UI Automation confirms the exact range is selected.
            if (!SelectionMatches(pattern, normalizedOriginal) || canCommit is not null && !canCommit()) return false;
            var shortcutFirst = !IsNativeEditHandle(handle);
            var firstPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (firstPaste && VerifyInsertedText(pattern, normalizedReplacement, valueVerification))
                return RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);

            // Chromium address bars expose a TextPattern range but ignore WM_PASTE.
            // Only fall back to a real paste chord if the original selection is still
            // intact, so a partial edit can never be duplicated.
            if (!SelectionMatches(pattern, normalizedOriginal) || canCommit is not null && !canCommit()) return false;
            Thread.Sleep(15);
            var retryPaste = shortcutFirst ? SendPasteShortcut() : handle != IntPtr.Zero && SendPaste(handle);
            if (retryPaste && VerifyInsertedText(pattern, normalizedReplacement, valueVerification))
                return RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);
            if (!SelectionMatches(pattern, normalizedOriginal) || canCommit is not null && !canCommit()) return false;
            var fallbackPaste = shortcutFirst ? handle != IntPtr.Zero && SendPaste(handle) : SendPasteShortcut();
            return fallbackPaste && VerifyInsertedText(pattern, normalizedReplacement, valueVerification) &&
                   RestoreCaretAfterPreservedNewlines(pattern, preservedNewlines);
        }
        finally
        {
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

    private static void TryMoveValueCaretToEnd(AutomationElement element)
    {
        try
        {
            element.SetFocus();
            SendInputBatch(
            [
                Create((ushort)NativeMethods.VkControl, 0, 0),
                Create((ushort)NativeMethods.VkEnd, 0, 0),
                Create((ushort)NativeMethods.VkEnd, 0, NativeMethods.KeyeventfKeyup),
                Create((ushort)NativeMethods.VkControl, 0, NativeMethods.KeyeventfKeyup)
            ]);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
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
        ClipboardSnapshot? prepared = null;
        try
        {
            var result = InvokeClipboard(() =>
            {
                var previous = CloneClipboardData(System.Windows.Clipboard.GetDataObject());
                var data = new System.Windows.DataObject();
                data.SetData(System.Windows.DataFormats.UnicodeText, text);
                data.SetData(ExcludeClipboardFormat, new byte[] { 0 });
                // Materialize the complete value now. Delayed rendering can expose only the
                // first character when the clipboard owner is restored shortly after paste.
                System.Windows.Clipboard.SetDataObject(data, true);
                if (!System.Windows.Clipboard.ContainsText() ||
                    !string.Equals(System.Windows.Clipboard.GetText(), text, StringComparison.Ordinal))
                    return false;
                prepared = new ClipboardSnapshot(previous);
                return true;
            });
            snapshot = prepared;
            return result;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException or InvalidComObjectException)
        {
            return false;
        }
    }

    private static void TryRestoreClipboard(ClipboardSnapshot snapshot, string injectedText)
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
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException or InvalidComObjectException) { }
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
