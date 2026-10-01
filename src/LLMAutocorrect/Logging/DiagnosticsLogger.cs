using System.Text;

namespace LLMAutocorrect.Logging;

public sealed class DiagnosticsLogger
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const long MaxBytes = 2 * 1024 * 1024;

    public DiagnosticsLogger(string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        _path = Path.Combine(logDirectory, "diagnostics.log");
    }

    public async Task WriteAsync(string eventName, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var safe = metadata is null ? "" : string.Join(' ', metadata.Select(kv => $"{Sanitize(kv.Key)}={Sanitize(kv.Value)}"));
        var line = $"{DateTimeOffset.Now:O} Event={Sanitize(eventName)} {safe}{Environment.NewLine}";
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                File.Move(_path, _path + ".old", true);
            await File.AppendAllTextAsync(_path, line, Encoding.UTF8);
        }
        finally { _gate.Release(); }
    }

    public string LogPath => _path;

    private static string Sanitize(object? value) =>
        (value?.ToString() ?? "null").Replace('\r', '_').Replace('\n', '_').Replace(' ', '_');
}
