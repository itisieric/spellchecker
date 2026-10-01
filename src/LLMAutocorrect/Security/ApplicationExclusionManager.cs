using LLMAutocorrect.Configuration;
using LLMAutocorrect.Autocomplete;

namespace LLMAutocorrect.Security;

public sealed class ApplicationExclusionManager
{
    private readonly SettingsManager _settings;
    public ApplicationExclusionManager(SettingsManager settings) => _settings = settings;

    public bool IsAutocorrectAllowed(string processName)
    {
        if (TerminalContext.IsTerminalProcess(processName, _settings.Current.TerminalProcesses)) return false;
        var rule = FindRule(processName);
        if (rule is not null) return rule.Autocorrect;
        return !_settings.Current.ExcludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAutocompleteAllowed(string processName)
    {
        var rule = FindRule(processName);
        if (rule is not null) return rule.Autocomplete;
        return !_settings.Current.ExcludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(string processName, bool autocorrect, bool autocomplete)
    {
        _settings.Current.ApplicationRules[processName] = new ApplicationRule
        {
            Autocorrect = autocorrect, Autocomplete = autocomplete
        };
        await _settings.SaveAsync();
    }

    private ApplicationRule? FindRule(string processName) => _settings.Current.ApplicationRules
        .FirstOrDefault(x => string.Equals(x.Key, processName, StringComparison.OrdinalIgnoreCase)).Value;
}
