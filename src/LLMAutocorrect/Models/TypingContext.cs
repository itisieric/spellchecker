namespace LLMAutocorrect.Models;

public sealed record WindowIdentity(IntPtr WindowHandle, int ProcessId, string ProcessName, string WindowTitle);

public sealed record FocusedControlIdentity(
    string? RuntimeId,
    bool IsPassword,
    bool IsEditable,
    string Name = "",
    string AutomationId = "",
    string ClassName = "")
{
    public bool IsBrowserAddressBar(string processName)
    {
        var browser = processName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) ||
                      processName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
                      processName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
                      processName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase);
        if (!browser) return false;
        return ClassName.Contains("Omnibox", StringComparison.OrdinalIgnoreCase) ||
               AutomationId.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               AutomationId.Contains("urlbar", StringComparison.OrdinalIgnoreCase) ||
               Name.Contains("address and search", StringComparison.OrdinalIgnoreCase) ||
               Name.Contains("address bar", StringComparison.OrdinalIgnoreCase) ||
               Name.Contains("enter address", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TypingSnapshot(
    long BufferVersion,
    WindowIdentity Window,
    string? FocusedElementId,
    string BufferText,
    DateTimeOffset Created,
    bool IsSynchronized);

public sealed record CorrectionSnapshot(
    long BufferVersion,
    IntPtr WindowHandle,
    int ProcessId,
    string? FocusedElementId,
    string OriginalTargetText,
    DateTimeOffset Created);
