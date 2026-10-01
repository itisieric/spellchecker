namespace LLMAutocorrect.Input;

public sealed class PhysicalKeyState
{
    private readonly HashSet<int> _pressed = new();
    private readonly object _gate = new();

    public void KeyDown(int virtualKey) { lock (_gate) _pressed.Add(virtualKey); }
    public void KeyUp(int virtualKey) { lock (_gate) _pressed.Remove(virtualKey); }
    public bool AnyPressed { get { lock (_gate) return _pressed.Count > 0; } }

    public bool WaitUntilReleased(TimeSpan timeout)
    {
        var started = Environment.TickCount64;
        while (AnyPressed)
        {
            if (Environment.TickCount64 - started >= timeout.TotalMilliseconds) return false;
            Thread.Sleep(5);
        }
        return true;
    }
}

