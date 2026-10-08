using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace LLMAutocorrect.Providers;

public sealed class ProviderUnavailableException : Exception
{
    public string ProviderName { get; }
    public int? StatusCode { get; }
    public string? ProviderStatus { get; }
    public bool IsRetryable { get; }
    public string UserMessage { get; }

    public ProviderUnavailableException(string message) : this(message, null, null, true,
        "The AI provider is temporarily unavailable. Please try again shortly.") { }

    internal ProviderUnavailableException(string message, int? statusCode, string? providerStatus,
        bool isRetryable, string userMessage, Exception? innerException = null,
        string providerName = "AI provider") : base(message, innerException)
    {
        ProviderName = providerName;
        StatusCode = statusCode;
        ProviderStatus = providerStatus;
        IsRetryable = isRetryable;
        UserMessage = userMessage;
    }
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

        return await GenerateJsonAsync(new Uri("https://generativelanguage.googleapis.com/v1beta/"),
            model, apiKey, systemInstruction, payload, responseSchema, cancellationToken);
    }

    internal async Task<JsonDocument> GenerateJsonAsync(
        Uri endpointBase, string model, string apiKey, string systemInstruction, object payload,
        object responseSchema, CancellationToken cancellationToken)
    {
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

        var endpoint = BuildEndpoint(endpointBase, $"models/{Uri.EscapeDataString(model)}:generateContent");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("x-goog-api-key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderUnavailableException("Gemini request timed out.", null, "TIMEOUT", true,
                "Gemini timed out. Please try again shortly.", ex, "Gemini");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException("Gemini network request failed.", null, "NETWORK_ERROR", true,
                "Gemini could not be reached. Check your internet connection and try again.", ex, "Gemini");
        }
        using (response)
        {
        if (!response.IsSuccessStatusCode)
        {
            var (providerStatus, providerMessage) = await ReadErrorAsync(response, cancellationToken);
            throw CreateHttpFailure(response.StatusCode, providerStatus, providerMessage);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var envelope = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryGetText(envelope.RootElement, out var json))
            throw new ProviderUnavailableException("Gemini returned no structured candidate.",
                (int)response.StatusCode, "NO_CANDIDATE", true,
                "Gemini returned no usable correction. Please try again.", providerName: "Gemini");
        return JsonDocument.Parse(json);
        }
    }

    internal static ProviderUnavailableException CreateHttpFailure(HttpStatusCode statusCode,
        string? providerStatus, string? providerMessage)
    {
        var code = (int)statusCode;
        var retryable = code is 408 or 429 or 500 or 502 or 503 or 504;
        var userMessage = code switch
        {
            401 => "The Gemini API key is invalid or expired. Update the API key in the app environment.",
            402 => "Gemini prepaid credits are depleted. Add credits or enable auto-reload in Google AI Studio.",
            403 => "The Gemini API key does not have permission to use this model or project.",
            404 => "The configured Gemini model was not found. Check the model setting.",
            429 => "Gemini's rate limit was reached. Please try again shortly.",
            >= 500 => "Gemini is temporarily unavailable. Please try again shortly.",
            _ => "Gemini rejected the request. Check the API key, model, and provider settings."
        };
        var safeProviderMessage = string.IsNullOrWhiteSpace(providerMessage)
            ? string.Empty
            : providerMessage.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (safeProviderMessage.Length > 300) safeProviderMessage = safeProviderMessage[..300];
        var detail = $"Gemini request failed with HTTP {code}" +
                     (string.IsNullOrWhiteSpace(providerStatus) ? string.Empty : $" ({providerStatus})") +
                     (safeProviderMessage.Length == 0 ? "." : $": {safeProviderMessage}");
        return new ProviderUnavailableException(detail, code, providerStatus, retryable, userMessage,
            providerName: "Gemini");
    }

    private static async Task<(string? Status, string? Message)> ReadErrorAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(value);
            if (!document.RootElement.TryGetProperty("error", out var error)) return (null, null);
            var status = error.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            var message = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            return (status, message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return (null, null); }
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

    internal static Uri BuildEndpoint(Uri endpointBase, string relative)
    {
        var value = endpointBase.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/');
        return new Uri(value, UriKind.Absolute);
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
