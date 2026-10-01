using System.Diagnostics;
using System.Text;
using LLMAutocorrect.Models;

namespace LLMAutocorrect.Windows;

public sealed class ForegroundWindowService
{
    public WindowIdentity GetCurrent()
    {
        var handle = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        var titleBuffer = new StringBuilder(Math.Max(1, NativeMethods.GetWindowTextLength(handle) + 1));
        NativeMethods.GetWindowText(handle, titleBuffer, titleBuffer.Capacity);
        string processName;
        try { processName = Process.GetProcessById((int)processId).ProcessName + ".exe"; }
        catch { processName = "unknown.exe"; }
        return new WindowIdentity(handle, (int)processId, processName, titleBuffer.ToString());
    }

    public bool Matches(IntPtr handle, int processId)
    {
        var current = NativeMethods.GetForegroundWindow();
        if (current != handle) return false;
        NativeMethods.GetWindowThreadProcessId(current, out var currentProcess);
        return currentProcess == processId;
    }
}

