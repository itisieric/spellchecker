using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;

namespace LLMAutocorrect.Memory;

public sealed partial class WritingMemory
{
    private const int MaximumPhraseEntries = 500;
    private const int MaximumSpellingEntries = 250;
    private readonly SettingsManager _settings;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private MemoryStore _store = new([], []);

    public string FilePath { get; }
    public int PhraseCount { get { lock (_gate) return _store.Phrases.Count; } }
    public int SpellingCount { get { lock (_gate) return _store.Spelling.Count; } }

    public WritingMemory(SettingsManager settings, string? filePath = null)
    {
        _settings = settings;
        FilePath = filePath ?? Path.Combine(settings.DataDirectory, "memory.json");
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            await using var stream = File.OpenRead(FilePath);
            var loaded = await JsonSerializer.DeserializeAsync<MemoryStore>(stream, cancellationToken: cancellationToken);
            if (loaded is not null)
                lock (_gate) _store = new(
                    loaded.Phrases.Take(MaximumPhraseEntries).ToList(),
                    loaded.Spelling.Take(MaximumSpellingEntries).ToList());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<string> RecallPhrases(string context, int maximum)
    {
        if (!_settings.Current.PersonalMemoryEnabled || maximum <= 0) return [];
        var keys = PrefixKeys(context);
        if (keys.Count == 0) return [];
        lock (_gate)
        {
            return keys.SelectMany((key, rank) => _store.Phrases
                    .Where(entry => entry.PrefixHash == key)
                    .Select(entry => (Entry: entry, Rank: rank)))
                .OrderBy(x => x.Rank)
                .ThenByDescending(x => x.Entry.AcceptedCount)
                .ThenByDescending(x => x.Entry.LastAcceptedUtc)
                .Select(x => x.Entry.Continuation)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maximum)
                .ToArray();
        }
    }

    public IReadOnlyList<string> GetSpellingHints(string target)
    {
        if (!_settings.Current.PersonalMemoryEnabled) return [];
        var words = Words().Matches(target).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            return words.Select(word => (Word: word, Hash: Hash("spell|" + word.ToLowerInvariant())))
                .Join(_store.Spelling, value => value.Hash, entry => entry.SourceHash,
                    (value, entry) => (value.Word, entry.Correction, entry.Count))
                .Where(x => x.Count >= 2)
                .OrderByDescending(x => x.Count)
                .Take(12)
                .Select(x => $"{x.Word} -> {x.Correction}")
                .ToArray();
        }
    }

    public async Task RecordAcceptedAsync(string context, string continuation)
    {
        if (!_settings.Current.PersonalMemoryEnabled) return;
        var cleaned = continuation.Trim();
        if (!CanPersist(cleaned) || Words().Matches(cleaned).Count > 12) return;
        var keys = PrefixKeys(context);
        if (keys.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var key in keys)
            {
                var existing = _store.Phrases.FirstOrDefault(entry =>
                    entry.PrefixHash == key && string.Equals(entry.Continuation, cleaned, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    _store.Phrases.Add(new(key, cleaned, 1, now));
                else
                {
                    existing.AcceptedCount++;
                    existing.LastAcceptedUtc = now;
                }
            }
            _store = new(_store.Phrases.OrderByDescending(x => x.AcceptedCount)
                .ThenByDescending(x => x.LastAcceptedUtc).Take(MaximumPhraseEntries).ToList(), _store.Spelling);
        }
        await SaveAsync();
    }

    public async Task RecordCorrectionAsync(string original, string corrected, IReadOnlyList<string> protectedTokens)
    {
        if (!_settings.Current.PersonalMemoryEnabled || !CanPersist(original) || !CanPersist(corrected)) return;
        var sourceWords = Words().Matches(original).Select(match => match.Value).ToArray();
        var correctedWords = Words().Matches(corrected).Select(match => match.Value).ToArray();
        if (sourceWords.Length != correctedWords.Length) return;

        var changed = false;
        lock (_gate)
        {
            for (var i = 0; i < sourceWords.Length; i++)
            {
                var source = sourceWords[i];
                var replacement = correctedWords[i];
                if (source.Length < 3 || replacement.Length < 2 ||
                    string.Equals(source, replacement, StringComparison.OrdinalIgnoreCase) ||
                    protectedTokens.Contains(source, StringComparer.Ordinal) ||
                    CorrectionValidator.Levenshtein(source.ToLowerInvariant(), replacement.ToLowerInvariant()) > Math.Max(2, source.Length / 2)) continue;
                var hash = Hash("spell|" + source.ToLowerInvariant());
                var existing = _store.Spelling.FirstOrDefault(entry => entry.SourceHash == hash &&
                    string.Equals(entry.Correction, replacement, StringComparison.OrdinalIgnoreCase));
                if (existing is null) _store.Spelling.Add(new(hash, replacement.ToLowerInvariant(), 1, DateTimeOffset.UtcNow));
                else { existing.Count++; existing.LastUsedUtc = DateTimeOffset.UtcNow; }
                changed = true;
            }
            if (changed)
                _store = new(_store.Phrases, _store.Spelling.OrderByDescending(x => x.Count)
                    .ThenByDescending(x => x.LastUsedUtc).Take(MaximumSpellingEntries).ToList());
        }
        if (changed) await SaveAsync();
    }

    public async Task ClearAsync()
    {
        lock (_gate) _store = new([], []);
        await _saveGate.WaitAsync();
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        finally { _saveGate.Release(); }
    }

    private async Task SaveAsync()
    {
        MemoryStore snapshot;
        lock (_gate) snapshot = new(_store.Phrases.Select(x => x.Copy()).ToList(), _store.Spelling.Select(x => x.Copy()).ToList());
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

    private static IReadOnlyList<string> PrefixKeys(string context)
    {
        var words = Words().Matches(context).Select(match => match.Value.ToLowerInvariant()).ToArray();
        var keys = new List<string>();
        for (var count = Math.Min(4, words.Length); count >= 2; count--)
            keys.Add(Hash("prefix|" + string.Join(' ', words[^count..])));
        return keys;
    }

    private static bool CanPersist(string text) => text.Length is > 0 and <= 100 &&
        !Sensitive().IsMatch(text) && !text.Any(char.IsDigit);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.Compiled)]
    private static partial Regex Words();
    [GeneratedRegex(@"https?://|@|[A-Za-z]:\\|/dev/|\b(password|passcode|pin|api\s*key|private\s*key|recovery\s*code|credit\s*card)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Sensitive();

    private sealed record MemoryStore(List<PhraseEntry> Phrases, List<SpellingEntry> Spelling);
    private sealed record PhraseEntry(string PrefixHash, string Continuation, int AcceptedCount, DateTimeOffset LastAcceptedUtc)
    {
        public int AcceptedCount { get; set; } = AcceptedCount;
        public DateTimeOffset LastAcceptedUtc { get; set; } = LastAcceptedUtc;
        public PhraseEntry Copy() => new(PrefixHash, Continuation, AcceptedCount, LastAcceptedUtc);
    }
    private sealed record SpellingEntry(string SourceHash, string Correction, int Count, DateTimeOffset LastUsedUtc)
    {
        public int Count { get; set; } = Count;
        public DateTimeOffset LastUsedUtc { get; set; } = LastUsedUtc;
        public SpellingEntry Copy() => new(SourceHash, Correction, Count, LastUsedUtc);
    }
}
