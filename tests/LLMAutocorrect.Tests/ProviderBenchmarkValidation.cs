using System.Text.Json;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;
using LLMAutocorrect.Security;

// Offline validation of synthetic provider measurements. Never connects to an
// editor, invokes SendInput, or reads application settings or personal memory.
internal static class ProviderBenchmarkValidation
{
    public static int Run(string samplesPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(samplesPath));
        var detector = new ProtectedTokenDetector();
        var validator = new CorrectionValidator(detector);
        var rows = new List<object>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var input = row.GetProperty("TestInput");
            var target = input.GetProperty("target_text").GetString()!;
            var suppliedTokens = input.GetProperty("protected_tokens").EnumerateArray()
                .Select(token => token.GetString()!).ToArray();
            var request = new CorrectionRequest(input.GetProperty("context_before").GetString()!, target,
                [], detector.Detect(target, suppliedTokens), CorrectionMode.Normal,
                PreserveCapitalizationAndPunctuation: input.GetProperty("preserve_capitalization_and_punctuation").GetBoolean());
            var raw = row.GetProperty("Result");
            var result = new CorrectionResult(raw.GetProperty("should_replace").GetBoolean(),
                raw.GetProperty("replacement").GetString()!, raw.GetProperty("change_type").GetString()!,
                raw.GetProperty("uncertain").GetBoolean(), raw.GetProperty("reason").GetString()!);
            var validation = validator.Validate(request, result, .4);
            var effectiveText = validation.IsValid ? result.Replacement.TrimEnd() : target;
            var expectedNoChange = row.GetProperty("ExpectedNoChange").GetBoolean();
            var passed = expectedNoChange ? effectiveText == target
                : validation.IsValid && row.GetProperty("QualityPass").GetBoolean();
            rows.Add(new
            {
                Profile = row.GetProperty("Profile").GetString(), Variant = row.GetProperty("Variant").GetString(),
                Case = row.GetProperty("Case").GetString(), Repeat = row.GetProperty("Repeat").GetInt32(),
                WallMs = row.GetProperty("WallMs").GetDouble(), OutputTokens = row.GetProperty("OutputTokens").GetInt32(),
                Input = target, EffectiveText = effectiveText, WillApply = validation.IsValid,
                Validation = validation.Reason, EditRatio = validation.EditRatio, Passed = passed,
                ExpectedNoChange = expectedNoChange, result.ShouldReplace, result.Uncertain, result.Reason
            });
        }
        var outputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(samplesPath))!, "validated-samples.json");
        File.WriteAllText(outputPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Validated {rows.Count} synthetic samples through the unchanged production CorrectionValidator.");
        Console.WriteLine(outputPath);
        return 0;
    }
}
