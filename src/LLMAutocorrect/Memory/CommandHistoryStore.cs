using System.Text.Json;
using LLMAutocorrect.Autocomplete;
using LLMAutocorrect.Configuration;

namespace LLMAutocorrect.Memory;

public sealed class CommandHistoryStore
{
    private readonly SettingsManager _settings;
    private readonly TerminalSuggestionValidator _validator;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<CommandEntry> _entries = [];
    private List<string> _powerShellHistory = [];

    public string FilePath { get; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    public CommandHistoryStore(SettingsManager settings, TerminalSuggestionValidator validator, string? filePath = null)
    {
        _settings = settings;
        _validator = validator;
        FilePath = filePath ?? Path.Combine(settings.DataDirectory, "terminal-history.json");
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(FilePath))
        {
            try
            {
                await using var stream = File.OpenRead(FilePath);
                var loaded = await JsonSerializer.DeserializeAsync<List<CommandEntry>>(stream, cancellationToken: cancellationToken);
                if (loaded is not null)
                    lock (_gate) _entries = loaded.Where(x => _validator.IsSafeHistoryEntry(x.Command))
                        .Take(_settings.Current.TerminalMaximumHistoryItems).ToList();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        LoadPowerShellHistory();
    }

    public IReadOnlyList<string> GetMatchingCommands(string shell, string currentCommand, int maximum)
    {
        if (maximum <= 0 || currentCommand.Length == 0 || !_validator.IsSafeContext(currentCommand)) return [];
        List<(string Command, int Count, DateTimeOffset LastUsed)> candidates;
        lock (_gate)
        {
            candidates = _entries
                .Where(entry => ShellMatches(shell, entry.Shell) &&
                    entry.Command.Length > currentCommand.Length &&
                    entry.Command.StartsWith(currentCommand, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (entry.Command, entry.Count, entry.LastUsedUtc))
                .ToList();
            if (_settings.Current.TerminalUsePowerShellHistory && shell is "powershell" or "windows-terminal")
                candidates.AddRange(_powerShellHistory
                    .Where(command => command.Length > currentCommand.Length &&
                        command.StartsWith(currentCommand, StringComparison.OrdinalIgnoreCase))
                    .Select((command, index) => (command, 1, DateTimeOffset.MinValue.AddTicks(index + 1))));
        }

        return candidates.Where(x => _validator.IsSafeHistoryEntry(x.Command))
            .GroupBy(x => x.Command, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Command = group.Key,
                Count = group.Sum(x => x.Count),
                LastUsed = group.Max(x => x.LastUsed)
            })
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.LastUsed)
            .Select(x => x.Command)
            .Take(maximum)
            .ToArray();
    }

    public async Task RecordAsync(string command, string shell)
    {
        var cleaned = command.Trim();
        if (!_settings.Current.TerminalRememberCommands || !_validator.IsSafeHistoryEntry(cleaned)) return;
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(entry =>
                string.Equals(entry.Shell, shell, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Command, cleaned, StringComparison.OrdinalIgnoreCase));
            if (existing is null) _entries.Add(new(cleaned, shell, 1, DateTimeOffset.UtcNow));
            else { existing.Count++; existing.LastUsedUtc = DateTimeOffset.UtcNow; }
            _entries = _entries.OrderByDescending(x => x.Count).ThenByDescending(x => x.LastUsedUtc)
                .Take(_settings.Current.TerminalMaximumHistoryItems).ToList();
        }
        await SaveAsync();
    }

    public async Task ClearAsync()
    {
        lock (_gate) _entries = [];
        await _saveGate.WaitAsync();
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        finally { _saveGate.Release(); }
    }

    private void LoadPowerShellHistory()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "PowerShell", "PSReadLine");
            if (!Directory.Exists(directory)) return;
            var commands = Directory.EnumerateFiles(directory, "*_history.txt")
                .SelectMany(path =>
                {
                    try { return File.ReadLines(path).TakeLast(1000); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
                })
                .Select(line => line.Trim())
                .Where(_validator.IsSafeHistoryEntry)
                .TakeLast(2000)
                .ToList();
            lock (_gate) _powerShellHistory = commands;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task SaveAsync()
    {
        List<CommandEntry> snapshot;
        lock (_gate) snapshot = _entries.Select(x => x.Copy()).ToList();
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _saveGate.Release(); }
    }

    private static bool ShellMatches(string current, string stored) =>
        current.Equals(stored, StringComparison.OrdinalIgnoreCase) ||
        current.Equals("windows-terminal", StringComparison.OrdinalIgnoreCase) ||
        stored.Equals("windows-terminal", StringComparison.OrdinalIgnoreCase);

    public sealed record CommandEntry(string Command, string Shell, int Count, DateTimeOffset LastUsedUtc)
    {
        public int Count { get; set; } = Count;
        public DateTimeOffset LastUsedUtc { get; set; } = LastUsedUtc;
        public CommandEntry Copy() => new(Command, Shell, Count, LastUsedUtc);
    }
}
