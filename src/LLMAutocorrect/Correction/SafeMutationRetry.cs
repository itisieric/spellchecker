namespace LLMAutocorrect.Correction;

internal sealed record MutationAttempt<T>(T Result, string? Diagnostic) where T : struct;

internal enum MutationStopReason
{
    None,
    CurrentnessLost,
    AmbiguousMutation,
    AttemptsExhausted
}

internal sealed record MutationRetryOutcome<T>(
    bool Succeeded,
    int Attempts,
    T? LastResult,
    IReadOnlyList<string> Diagnostics,
    MutationStopReason StopReason) where T : struct
{
    // The attempt ran, but its diagnostic could not prove that no mutation
    // occurred. Callers that mirror editor text must stop trusting that mirror.
    public bool ShouldInvalidateTrackedText =>
        Attempts > 0 && StopReason == MutationStopReason.AmbiguousMutation;
}

internal static class SafeMutationRetry
{
    internal static bool CanAttempt(Func<bool> operationIsCurrent, Func<bool> waitForPhysicalKeysReleased) =>
        operationIsCurrent() && waitForPhysicalKeysReleased() && operationIsCurrent();

    public static async Task<MutationRetryOutcome<T>> RunAsync<T>(
        int retryAttempts,
        int retryDelayMs,
        Func<bool> canAttempt,
        Func<MutationAttempt<T>> attempt,
        Func<T, bool> succeeded,
        Func<T, string?, bool>? safeToRetry,
        CancellationToken cancellationToken) where T : struct
    {
        var maximumAttempts = 1 + Math.Clamp(retryAttempts, 0, 3);
        var delay = Math.Clamp(retryDelayMs, 100, 3000);
        var diagnostics = new List<string>(maximumAttempts);
        T? lastResult = null;

        for (var index = 0; index < maximumAttempts; index++)
        {
            if (index > 0) await Task.Delay(delay, cancellationToken);
            if (!canAttempt())
                return new(false, index, lastResult, diagnostics, MutationStopReason.CurrentnessLost);

            var current = attempt();
            lastResult = current.Result;
            diagnostics.Add($"attempt={index + 1} {current.Diagnostic ?? "stage=unspecified"}");
            if (succeeded(current.Result))
                return new(true, index + 1, lastResult, diagnostics, MutationStopReason.None);
            if (safeToRetry is not null && !safeToRetry(current.Result, current.Diagnostic))
                return new(false, index + 1, lastResult, diagnostics, MutationStopReason.AmbiguousMutation);
        }

        return new(false, maximumAttempts, lastResult, diagnostics, MutationStopReason.AttemptsExhausted);
    }
}
