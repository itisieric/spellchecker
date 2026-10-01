namespace LLMAutocorrect.Configuration;

public sealed class TechnicalDictionary
{
    private static readonly string[] Defaults =
    [
        "Modbus", "RTU", "RS485", "VFD", "Teensy", "MediaMTX", "Flutter", "Arduino",
        "Raspberry Pi", "A1VFD", "serverDataScreenGetList", "FFmpeg", "CUDA", "PyTorch", "Brave"
    ];

    private readonly string _path;
    public IReadOnlyList<string> Terms { get; private set; } = Defaults;

    public TechnicalDictionary(SettingsManager settings) =>
        _path = Path.Combine(settings.DataDirectory, "dictionary.txt");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllLinesAsync(_path, Defaults, cancellationToken);
        }

        Terms = (await File.ReadAllLinesAsync(_path, cancellationToken))
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    public string FilePath => _path;
}

