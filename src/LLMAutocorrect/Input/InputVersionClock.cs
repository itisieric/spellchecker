namespace LLMAutocorrect.Input;

public sealed class InputVersionClock
{
    private long _value;
    public long Current => Interlocked.Read(ref _value);
    public long Increment() => Interlocked.Increment(ref _value);
}

