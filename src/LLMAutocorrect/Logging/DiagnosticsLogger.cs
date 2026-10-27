using System.Text;
using System.Diagnostics;
using System.Globalization;

namespace LLMAutocorrect.Logging;

public sealed class DiagnosticsLogger
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<OperationContext?> _operation = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..16];
    private readonly string _buildId = typeof(DiagnosticsLogger).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    private readonly object _rateGate = new();
    private readonly Dictionary<string, (long Timestamp, int Suppressed)> _rateLimits = new();
    private long _nextOperation;
    private int _failedWrites;
    private string? _lastWriteFailure;
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly string[] SensitiveParts =
        ["ApiKey", "Secret", "Authorization", "Prompt", "OriginalText", "ReplacementText", "TypedText", "Instructions", "AccessToken", "BearerToken"];

    public DiagnosticsLogger(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "logs", "diagnostics.log");
    }

    public IDisposable BeginOperation(string kind, string process, long inputVersion)
    {
        var previous = _operation.Value;
        _operation.Value = new OperationContext(Interlocked.Increment(ref _nextOperation), kind, process, inputVersion);
        return new OperationScope(this, previous);
    }

    public bool OperationContactedProvider => _operation.Value?.ProviderRequested == true;

    public void SetOperationStage(string stage)
    {
        if (_operation.Value is { } operation) operation.Stage = stage;
    }

    public void MarkProviderRequest()
    {
        if (_operation.Value is not { } operation) return;
        operation.ProviderRequests++;
        operation.Stage = "provider";
    }

    public Task WriteRateLimitedAsync(string eventName, string key, IReadOnlyDictionary<string, object?> metadata,
        TimeSpan? interval = null)
    {
        var now = Stopwatch.GetTimestamp();
        var suppressed = 0;
        lock (_rateGate)
        {
            if (_rateLimits.TryGetValue(key, out var last))
            {
                if (Stopwatch.GetElapsedTime(last.Timestamp, now) < (interval ?? TimeSpan.FromSeconds(10)))
                {
                    _rateLimits[key] = (last.Timestamp, last.Suppressed + 1);
                    return Task.CompletedTask;
                }
                suppressed = last.Suppressed;
            }
            if (_rateLimits.Count >= 128 && !_rateLimits.ContainsKey(key)) _rateLimits.Clear();
            _rateLimits[key] = (now, 0);
        }
        var fields = new Dictionary<string, object?>(metadata) { ["SuppressedEvents"] = suppressed };
        return WriteAsync(eventName, fields);
    }

    public async Task WriteAsync(string eventName, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var safe = metadata is null ? "" : string.Join(' ', metadata.Select(kv =>
            $"{Sanitize(kv.Key)}={(IsSensitiveField(kv.Key) ? "redacted" : Sanitize(kv.Value))}"));
        var context = _operation.Value;
        var operation = context is null ? string.Empty :
            $" OperationId={context.Id} Operation={Sanitize(context.Kind)} OperationMs={Stopwatch.GetElapsedTime(context.Started).TotalMilliseconds:F0}" +
            $" OperationProcess={Sanitize(context.Process)} InputVersion={context.InputVersion}" +
            $" Stage={Sanitize(context.Stage)} ProviderRequested={context.ProviderRequested} ProviderAttempt={context.ProviderRequests}";
        var prefix = $"{DateTimeOffset.Now:O} Event={Sanitize(eventName)} SessionId={_sessionId} BuildId={_buildId}{operation}";
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                File.Move(_path, _path + ".old", true);
            var recovered = _failedWrites == 0 ? string.Empty :
                $" DroppedLogWrites={_failedWrites} PriorLogFailure={_lastWriteFailure}";
            var line = $"{prefix}{recovered} {safe}{Environment.NewLine}";
            await File.AppendAllTextAsync(_path, line, Encoding.UTF8);
            Volatile.Write(ref _failedWrites, 0);
            _lastWriteFailure = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A full disk or locked log must never turn an applied correction into
            // a provider retry. Report missed writes on the next successful entry.
            Interlocked.Increment(ref _failedWrites);
            _lastWriteFailure = ex.GetType().Name;
        }
        finally { _gate.Release(); }
    }

    public string LogPath => _path;
    public int FailedWrites => Volatile.Read(ref _failedWrites);

    private static bool IsSensitiveField(string key)
    {
        var normalized = key.Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized.Equals("Message", StringComparison.OrdinalIgnoreCase) ||
               SensitiveParts.Any(part => normalized.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sanitize(object? value) =>
        (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null")
        .Replace('\r', '_').Replace('\n', '_').Replace(' ', '_').Replace('\t', '_');

    private sealed class OperationContext(long id, string kind, string process, long inputVersion)
    {
        public long Id { get; } = id;
        public string Kind { get; } = kind;
        public string Process { get; } = process;
        public long InputVersion { get; } = inputVersion;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public string Stage { get; set; } = "preflight";
        public int ProviderRequests { get; set; }
        public bool ProviderRequested => ProviderRequests > 0;
    }

    private sealed class OperationScope(DiagnosticsLogger owner, OperationContext? previous) : IDisposable
    {
        public void Dispose() => owner._operation.Value = previous;
    }
}
