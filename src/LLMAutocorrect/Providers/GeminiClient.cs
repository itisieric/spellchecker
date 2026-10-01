using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace LLMAutocorrect.Providers;

public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException(string message) : base(message) { }
}

public sealed class GeminiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GeminiClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<JsonDocument> GenerateJsonAsync(
        string model, string systemInstruction, object payload, object responseSchema,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ProviderUnavailableException("GEMINI_API_KEY is not configured.");

        var generationConfig = new Dictionary<string, object>
        {
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = responseSchema,
            ["thinkingConfig"] = model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase)
                ? new { thinkingBudget = 0 }
                : model.Contains("flash-lite", StringComparison.OrdinalIgnoreCase)
                    ? new { thinkingLevel = "minimal" }
                    : new { thinkingLevel = "low" }
        };
        var requestBody = new
        {
            systemInstruction = new { parts = new[] { new { text = systemInstruction } } },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = JsonSerializer.Serialize(payload) } } }
            },
            generationConfig
        };

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("x-goog-api-key", apiKey);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ProviderUnavailableException($"Gemini request failed with HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var envelope = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryGetText(envelope.RootElement, out var json))
            throw new ProviderUnavailableException("Gemini returned no structured candidate.");
        return JsonDocument.Parse(json);
    }

    private static bool TryGetText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) return false;
        var first = candidates[0];
        if (!first.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0 ||
            !parts[0].TryGetProperty("text", out var value)) return false;
        text = value.GetString() ?? string.Empty;
        return text.Length > 0;
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
