namespace LLMAutocorrect.Correction;

public sealed class CorrectionHistory
{
    private readonly LinkedList<CorrectionHistoryEntry> _entries = new();
    private readonly object _gate = new();
    private readonly int _capacity;

    public CorrectionHistory(int capacity = 20) => _capacity = capacity;

    public void Add(CorrectionHistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > _capacity) _entries.RemoveLast();
        }
    }

    public CorrectionHistoryEntry? Peek()
    {
        lock (_gate) return _entries.First?.Value;
    }

    public void RemoveLatest()
    {
        lock (_gate) { if (_entries.First is not null) _entries.RemoveFirst(); }
    }
}

