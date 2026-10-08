using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMAutocorrect.Configuration;

public sealed class SettingsManager
{
    internal const int CurrentSettingsVersion = 6;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public AppSettings Current { get; private set; } = new();

    public SettingsManager(string? dataDirectory = null) => DataDirectory = dataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLMAutocorrect");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(SettingsPath))
        {
            Current.SettingsVersion = CurrentSettingsVersion;
            await SaveAsync(cancellationToken);
            return;
        }

        var migrationRequired = false;
        try
        {
            await using (var stream = File.OpenRead(SettingsPath))
                Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                          ?? new AppSettings();
            if (Current.SettingsVersion < CurrentSettingsVersion)
            {
                if (string.Equals(Current.GeminiModel, "gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                    Current.GeminiModel = "gemini-3.5-flash-lite";
                Current.GeminiProvider ??= AiProviderDefaults.Create(AiProviderKind.Gemini);
                Current.GeminiProvider.Model = Current.GeminiModel;
                Current.OllamaProvider ??= AiProviderDefaults.Create(AiProviderKind.Ollama);
                Current.LMStudioProvider ??= AiProviderDefaults.Create(AiProviderKind.LMStudio);
                Current.OpenAICompatibleProvider ??= AiProviderDefaults.Create(AiProviderKind.OpenAICompatible);
                // Version 6 introduces the requested local-first default. Gemini remains
                // configured and can be selected again without losing its model setting.
                Current.Provider = AiProviderKind.Ollama;
                // Keep a short final quiet period so typing that resumes while a
                // request finishes cancels the correction before text is selected.
                Current.CorrectionCommitDelayMs = 100;
                Current.SettingsVersion = CurrentSettingsVersion;
                migrationRequired = true;
            }
        }
        catch (JsonException)
        {
            Current = new AppSettings();
        }
        NormalizeProviders(Current);
        if (migrationRequired) await SaveAsync(cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = SettingsPath + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, Current, JsonOptions, cancellationToken);
        File.Move(temp, SettingsPath, true);
    }

    private static void NormalizeProviders(AppSettings settings)
    {
        settings.OllamaProvider ??= AiProviderDefaults.Create(AiProviderKind.Ollama);
        settings.LMStudioProvider ??= AiProviderDefaults.Create(AiProviderKind.LMStudio);
        settings.GeminiProvider ??= AiProviderDefaults.Create(AiProviderKind.Gemini);
        settings.OpenAICompatibleProvider ??= AiProviderDefaults.Create(AiProviderKind.OpenAICompatible);
        foreach (var kind in Enum.GetValues<AiProviderKind>())
        {
            var profile = settings.GetProvider(kind);
            var defaults = AiProviderDefaults.Create(kind);
            if (string.IsNullOrWhiteSpace(profile.Endpoint)) profile.Endpoint = defaults.Endpoint;
            if (string.IsNullOrWhiteSpace(profile.Model)) profile.Model = defaults.Model;
            profile.ContextLength = Math.Clamp(profile.ContextLength, 512, 32768);
            if (string.IsNullOrWhiteSpace(profile.AuthenticationHeader))
                profile.AuthenticationHeader = defaults.AuthenticationHeader;
        }
    }
}
