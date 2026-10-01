namespace LLMAutocorrect.Autocomplete;

public sealed record TerminalAutocompleteRequest(
    string Shell,
    string CurrentCommand,
    string WindowTitle,
    IReadOnlyList<string> RecentSessionCommands,
    IReadOnlyList<string> MatchingHistory,
    int MaxSuggestions,
    string CustomInstructions);

public interface ITerminalAutocompleteProvider
{
    Task<AutocompleteResult> PredictAsync(TerminalAutocompleteRequest request, CancellationToken cancellationToken);
}

public static class TerminalContext
{
    public static bool IsTerminalProcess(string processName, IEnumerable<string> configuredProcesses) =>
        configuredProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public static string DetectShell(string processName, string windowTitle)
    {
        if (processName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)) return "powershell";
        if (processName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.StartsWith("cmd", StringComparison.OrdinalIgnoreCase)) return "cmd";
        return "windows-terminal";
    }

    public static string CurrentLine(string bufferText)
    {
        var end = bufferText.Length;
        while (end > 0 && bufferText[end - 1] is '\r' or '\n') end--;
        if (end != bufferText.Length) return string.Empty;
        var start = bufferText.LastIndexOf('\n') + 1;
        return bufferText[start..].TrimStart();
    }

    public static string? LastCompletedCommand(string bufferText)
    {
        if (bufferText.Length == 0 || bufferText[^1] != '\n') return null;
        var end = bufferText.Length - 1;
        while (end > 0 && bufferText[end - 1] == '\r') end--;
        var start = bufferText.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        var command = bufferText[start..end].Trim();
        return command.Length == 0 ? null : command;
    }

    public static IReadOnlyList<string> RecentSessionCommands(string bufferText, int maximum)
    {
        if (maximum <= 0) return [];
        return bufferText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .TakeLast(maximum)
            .ToArray();
    }
}
