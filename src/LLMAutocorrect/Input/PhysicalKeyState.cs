using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Input;

public sealed class PhysicalKeyState
{
    private static readonly TimeSpan ReconciliationGrace = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<int, long> _pressed = new();
    private readonly object _gate = new();
    private readonly Func<int, bool> _isPhysicallyDown;
    private readonly TimeSpan _reconciliationGrace;
    private int[] _lastTimedOutKeys = [];

    public PhysicalKeyState() : this(virtualKey =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0, ReconciliationGrace) { }

    internal PhysicalKeyState(Func<int, bool> isPhysicallyDown, TimeSpan? reconciliationGrace = null)
    {
        _isPhysicallyDown = isPhysicallyDown ?? throw new ArgumentNullException(nameof(isPhysicallyDown));
        _reconciliationGrace = reconciliationGrace ?? ReconciliationGrace;
    }

    public void KeyDown(int virtualKey)
    {
        lock (_gate) _pressed.TryAdd(virtualKey, Environment.TickCount64);
    }
    public void KeyUp(int virtualKey) { lock (_gate) _pressed.Remove(virtualKey); }
    public bool AnyPressed { get { lock (_gate) return _pressed.Count > 0; } }
    public string LastWaitDiagnostic
    {
        get
        {
            lock (_gate)
                return _lastTimedOutKeys.Length == 0
                    ? "pressedKeyCount=0"
                    : $"pressedKeyCount={_lastTimedOutKeys.Length} pressedVirtualKeys={string.Join(',', _lastTimedOutKeys)}";
        }
    }

    public bool WaitUntilReleased(TimeSpan timeout)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            lock (_gate)
            {
                // Low-level hooks can occasionally miss a key-up transition when
                // Windows switches desktops, resumes, or replaces a hook. Trust the
                // operating system's current high bit before treating our cached set
                // as a held key; otherwise one stale entry can block every correction
                // until the application is restarted.
                var now = Environment.TickCount64;
                var staleKeys = _pressed
                    .Where(pair => now - pair.Value >= _reconciliationGrace.TotalMilliseconds &&
                                   !_isPhysicallyDown(pair.Key))
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (var staleKey in staleKeys) _pressed.Remove(staleKey);
                if (_pressed.Count == 0)
                {
                    _lastTimedOutKeys = [];
                    return true;
                }

                if (Environment.TickCount64 - started >= timeout.TotalMilliseconds)
                {
                    _lastTimedOutKeys = _pressed.Keys.OrderBy(key => key).ToArray();
                    return false;
                }
            }
            Thread.Sleep(5);
        }
    }
}
