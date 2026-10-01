using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMAutocorrect.Configuration;

public sealed class SettingsManager
{
    private const int CurrentSettingsVersion = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLMAutocorrect");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public AppSettings Current { get; private set; } = new();

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
}
