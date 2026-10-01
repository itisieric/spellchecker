using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Input;

public sealed record KeyboardInputEvent(long InputVersion, int VirtualKey, char? Character, bool Control, bool Alt, bool Shift, DateTimeOffset Timestamp);

public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private readonly InputVersionClock _clock;
    private readonly PhysicalKeyState _physicalKeys;
    private readonly HashSet<int> _suppressedKeys = new();
    private IntPtr _hook;
    public Func<KeyboardInputEvent, bool>? InputReceived { get; set; }

    public GlobalKeyboardHook(InputVersionClock clock, PhysicalKeyState physicalKeys)
    {
        _clock = clock;
        _physicalKeys = physicalKeys;
        _callback = HookCallback;
    }

    public void Start()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback,
            NativeMethods.GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
            var injected = (data.Flags & NativeMethods.LlkhfInjected) != 0 || data.ExtraInfo == NativeMethods.InjectionMarker;
            if (!injected && (wParam == NativeMethods.WmKeyUp || wParam == NativeMethods.WmSysKeyUp))
            {
                _physicalKeys.KeyUp((int)data.VkCode);
                if (_suppressedKeys.Remove((int)data.VkCode)) return new IntPtr(1);
            }
            else if (!injected && (wParam == NativeMethods.WmKeyDown || wParam == NativeMethods.WmSysKeyDown))
            {
                _physicalKeys.KeyDown((int)data.VkCode);
                // A modifier press is part of the following shortcut, not an edit.
                // Advancing the input version here would make Alt+Down and
                // Ctrl+Right invalidate the suggestion before the second key arrives.
                if (IsModifierKey((int)data.VkCode))
                    return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
                var control = IsDown(NativeMethods.VkControl);
                var alt = IsDown(NativeMethods.VkMenu);
                var shift = IsDown(NativeMethods.VkShift);
                var character = control || alt ? null : Translate(data.VkCode, data.ScanCode);
                var nextVersion = _clock.Current + 1;
                var handled = InputReceived?.Invoke(new(nextVersion, (int)data.VkCode, character, control, alt, shift, DateTimeOffset.UtcNow)) == true;
                if (handled)
                {
                    _suppressedKeys.Add((int)data.VkCode);
                    return new IntPtr(1);
                }
                _clock.Increment();
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    internal static bool IsModifierKey(int key) =>
        key is NativeMethods.VkControl or NativeMethods.VkMenu or NativeMethods.VkShift or
            NativeMethods.VkLeftShift or NativeMethods.VkRightShift or
            NativeMethods.VkLeftControl or NativeMethods.VkRightControl or
            NativeMethods.VkLeftMenu or NativeMethods.VkRightMenu;

    private static char? Translate(uint key, uint scanCode)
    {
        var state = new byte[256];
        if (!NativeMethods.GetKeyboardState(state)) return null;
        var buffer = new StringBuilder(8);
        var count = NativeMethods.ToUnicodeEx(key, scanCode, state, buffer, buffer.Capacity, 0, NativeMethods.GetKeyboardLayout(0));
        return count == 1 ? buffer[0] : null;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
