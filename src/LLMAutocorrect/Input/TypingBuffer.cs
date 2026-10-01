using System.Text;
using LLMAutocorrect.Models;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Input;

public sealed record BufferChange(TypingSnapshot Snapshot, bool ShouldTriggerImmediately);

public sealed class TypingBuffer
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly int _maximumCharacters;
    private WindowIdentity _window = new(IntPtr.Zero, 0, "unknown.exe", string.Empty);
    private bool _synchronized;
    private long _version;

    public TypingBuffer(int maximumCharacters = 1500) => _maximumCharacters = maximumCharacters;

    public BufferChange Handle(KeyboardInputEvent input, WindowIdentity window)
    {
        lock (_gate)
        {
            _version = input.InputVersion;
            if (window.WindowHandle != _window.WindowHandle || window.ProcessId != _window.ProcessId)
            {
                _text.Clear();
                _window = window;
                _synchronized = false;
            }

            var key = input.VirtualKey;
            if (input.Control && key is NativeMethods.VkA or NativeMethods.VkC or NativeMethods.VkV or NativeMethods.VkX)
            {
                ResetUnsafe(window);
                return Change(false);
            }

            if (key is NativeMethods.VkLeft or NativeMethods.VkRight or NativeMethods.VkUp or NativeMethods.VkDown or
                NativeMethods.VkHome or NativeMethods.VkEnd or NativeMethods.VkPrior or NativeMethods.VkNext or
                NativeMethods.VkDelete or NativeMethods.VkTab or NativeMethods.VkEscape)
            {
                ResetUnsafe(window);
                return Change(false);
            }

            if (key == NativeMethods.VkBack)
            {
                // A plain Backspace at the tracked caret removes exactly one known
                // character. Navigation, mouse clicks, selections, Delete, and
                // Ctrl+Backspace already invalidate the buffer through other paths.
                // Keeping the remaining suffix synchronized lets correction resume
                // after the user fixes a typo and continues typing.
                if (input.Control || input.Alt || !_synchronized || _text.Length == 0)
                    ResetUnsafe(window);
                else
                    _text.Length--;
                return Change(false);
            }

            if (key == NativeMethods.VkReturn)
            {
                EnsureNewSegment();
                _text.Append('\n');
                TrimUnsafe();
                return Change(true);
            }

            if (input.Character is { } character && !char.IsControl(character))
            {
                EnsureNewSegment();
                _text.Append(character);
                TrimUnsafe();
                return Change(character is '.' or '?' or '!');
            }

            return Change(false);
        }
    }

    public TypingSnapshot Invalidate(WindowIdentity window, long version)
    {
        lock (_gate)
        {
            _version = version;
            ResetUnsafe(window);
            return SnapshotUnsafe();
        }
    }

    public TypingSnapshot GetSnapshot()
    {
        lock (_gate) return SnapshotUnsafe();
    }

    public bool TryReplaceSuffix(long expectedVersion, string original, string replacement, long resultingVersion)
    {
        lock (_gate)
        {
            if (_version != expectedVersion || !_synchronized || !_text.ToString().EndsWith(original, StringComparison.Ordinal)) return false;
            _text.Length -= original.Length;
            _text.Append(replacement);
            _version = resultingVersion;
            TrimUnsafe();
            return true;
        }
    }

    public bool TryAppend(long expectedVersion, string value, long resultingVersion)
    {
        lock (_gate)
        {
            if (_version != expectedVersion || !_synchronized) return false;
            _text.Append(value);
            _version = resultingVersion;
            TrimUnsafe();
            return true;
        }
    }

    private void EnsureNewSegment()
    {
        if (_synchronized) return;
        _text.Clear();
        _synchronized = true;
    }

    private void ResetUnsafe(WindowIdentity window)
    {
        _text.Clear();
        _window = window;
        _synchronized = false;
    }

    private void TrimUnsafe()
    {
        if (_text.Length > _maximumCharacters) _text.Remove(0, _text.Length - _maximumCharacters);
    }

    private BufferChange Change(bool immediate) => new(SnapshotUnsafe(), immediate);
    private TypingSnapshot SnapshotUnsafe() => new(_version, _window, null, _text.ToString(), DateTimeOffset.UtcNow, _synchronized);
}
