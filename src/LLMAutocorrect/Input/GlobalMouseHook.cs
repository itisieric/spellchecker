using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Input;

public sealed class GlobalMouseHook : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private readonly InputVersionClock _clock;
    private IntPtr _hook;
    public Action? Clicked { get; set; }
    public Action<System.Windows.Point>? LeftClicked { get; set; }
    public Action<System.Windows.Point>? RightClicked { get; set; }
    public GlobalMouseHook(InputVersionClock clock)
    {
        _clock = clock;
        _callback = HookCallback;
    }

    public void Start()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _callback,
            NativeMethods.GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == NativeMethods.WmLButtonDown || wParam == NativeMethods.WmRButtonDown))
        {
            _clock.Increment();
            Clicked?.Invoke();
            if (wParam == NativeMethods.WmLButtonDown)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MsLlHookStruct>(lParam);
                LeftClicked?.Invoke(new System.Windows.Point(data.Point.X, data.Point.Y));
            }
        }
        else if (code >= 0 && wParam == NativeMethods.WmRButtonUp)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MsLlHookStruct>(lParam);
            RightClicked?.Invoke(new System.Windows.Point(data.Point.X, data.Point.Y));
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
