using Microsoft.Win32;

namespace LLMAutocorrect.UI;

public sealed class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
            key?.SetValue("LLMAutocorrect", $"\"{executable}\"");
        }
        else key?.DeleteValue("LLMAutocorrect", false);
    }
}

