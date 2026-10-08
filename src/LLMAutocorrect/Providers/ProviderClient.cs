using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Security;

namespace LLMAutocorrect.Providers;

public interface IJsonGenerationClient
{
    Task<JsonDocument> GenerateJsonAsync(string systemInstruction, object payload, object responseSchema,
        CancellationToken cancellationToken);
}

public sealed record ProviderConnectionTestResult(bool Success, string Message);

public interface IProviderConnectionTester
{
    Task<ProviderConnectionTestResult> TestAsync(AiProviderKind kind, AiProviderSettings settings,
        string? unsavedSecret, CancellationToken cancellationToken);
}

public sealed class ProviderClient : IJsonGenerationClient, IProviderConnectionTester, IDisposable
{
    private readonly SettingsManager _settings;
    private readonly IProviderSecretStore _secrets;
    private readonly HttpClient _httpClient;
    private readonly GeminiClient _geminiClient;
    private readonly bool _ownsHttpClient;

    public ProviderClient(SettingsManager settings, IProviderSecretStore secrets, HttpClient? httpClient = null)
    {
        _settings = settings;
        _secrets = secrets;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        // A local model's first request can include a cold GPU load. Subsequent
        // requests are fast when KeepModelLoaded is enabled, but the initial load
        // must not be mistaken for a dead provider.
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
        _geminiClient = new GeminiClient(_httpClient);
    }

    public async Task<JsonDocument> GenerateJsonAsync(string systemInstruction, object payload, object responseSchema,
        CancellationToken cancellationToken)
    {
        var kind = _settings.Current.Provider;
        var profile = _settings.Current.GetProvider(kind).Clone();
        if (!ProviderEndpointPolicy.TryValidate(profile.Endpoint, out var endpoint, out var error))
            throw ConfigurationFailure(kind, error);
        if (_settings.Current.PrivateMode && !ProviderEndpointPolicy.IsSameComputer(endpoint))
            throw ConfigurationFailure(kind,
                "Private Mode blocked this provider because it is not running on this computer.");
        var secret = ResolveSecret(kind, null);
        return kind switch
        {
            AiProviderKind.Ollama => await GenerateOllamaAsync(kind, profile, endpoint, secret,
                systemInstruction, payload, responseSchema, cancellationToken),
            AiProviderKind.Gemini => await GenerateGeminiAsync(profile, endpoint, secret,
                systemInstruction, payload, responseSchema, cancellationToken),
            _ => await GenerateOpenAiCompatibleAsync(kind, profile, endpoint, secret,
                systemInstruction, payload, responseSchema, cancellationToken)
        };
    }

    public async Task<ProviderConnectionTestResult> TestAsync(AiProviderKind kind, AiProviderSettings profile,
        string? unsavedSecret, CancellationToken cancellationToken)
    {
        if (!ProviderEndpointPolicy.TryValidate(profile.Endpoint, out var endpoint, out var error))
            return new(false, error);
        try
        {
            var secret = ResolveSecret(kind, unsavedSecret);
            var uri = kind switch
            {
                AiProviderKind.Ollama => Append(endpoint, "api/tags"),
                AiProviderKind.Gemini => GeminiClient.BuildEndpoint(endpoint,
                    $"models/{Uri.EscapeDataString(profile.Model)}"),
                _ => Append(endpoint, "models")
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyAuthentication(request, kind, profile, secret);
            using var response = await SendAsync(request, kind, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(false, CreateFailure(kind, response).UserMessage);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (kind == AiProviderKind.Gemini)
                return new(true, $"Connected to Gemini model {profile.Model}.");
            var models = ReadModelNames(kind, document.RootElement);
            if (models.Any(model => ModelMatches(model, profile.Model)))
                return new(true, $"Connected. Model {profile.Model} is available.");
            var hint = kind == AiProviderKind.Ollama
                ? $" Run: ollama pull {profile.Model}"
                : " Load or download that model in the provider first.";
            return new(false, $"Connected, but model {profile.Model} was not found.{hint}");
        }
        catch (ProviderUnavailableException ex) { return new(false, ex.UserMessage); }
        catch (JsonException) { return new(false, "The provider responded, but its model list was not valid JSON."); }
    }

    private async Task<JsonDocument> GenerateGeminiAsync(AiProviderSettings profile, Uri endpoint, string? secret,
        string systemInstruction, object payload, object schema, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw ConfigurationFailure(AiProviderKind.Gemini,
                "No Gemini API key is saved. Add it in Settings - AI Provider.");
        return await _geminiClient.GenerateJsonAsync(endpoint, profile.Model, secret, systemInstruction,
            payload, schema, cancellationToken);
    }

    private async Task<JsonDocument> GenerateOllamaAsync(AiProviderKind kind, AiProviderSettings profile,
        Uri endpoint, string? secret, string systemInstruction, object payload, object schema,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = profile.Model,
            messages = Messages(systemInstruction, payload),
            stream = false,
            think = false,
            format = NormalizeSchema(schema),
            keep_alive = profile.KeepModelLoaded ? (object)(-1) : "5m",
            options = new
            {
                temperature = 0,
                num_ctx = Math.Clamp(profile.ContextLength, 512, 32768),
                num_predict = 512
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Append(endpoint, "api/chat"))
        {
            Content = JsonContent.Create(requestBody)
        };
        ApplyAuthentication(request, kind, profile, secret);
        using var response = await SendAsync(request, kind, cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateFailure(kind, response);
        using var envelope = await ReadJsonAsync(kind, response, cancellationToken);
        if (!envelope.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw InvalidResponse(kind, "Ollama returned no message content.");
        return ParseGeneratedJson(kind, content.GetString());
    }

    private async Task<JsonDocument> GenerateOpenAiCompatibleAsync(AiProviderKind kind,
        AiProviderSettings profile, Uri endpoint, string? secret, string systemInstruction, object payload,
        object schema, CancellationToken cancellationToken)
    {
        var normalizedSchema = NormalizeSchema(schema);
        var requestBody = new
        {
            model = profile.Model,
            messages = Messages(systemInstruction, payload),
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "spellchecker_response", strict = true, schema = normalizedSchema }
            },
            temperature = 0,
            max_tokens = 512,
            stream = false
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Append(endpoint, "chat/completions"))
        {
            Content = JsonContent.Create(requestBody)
        };
        ApplyAuthentication(request, kind, profile, secret);
        using var response = await SendAsync(request, kind, cancellationToken);
        if (!response.IsSuccessStatusCode) throw CreateFailure(kind, response);
        using var envelope = await ReadJsonAsync(kind, response, cancellationToken);
        if (!envelope.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw InvalidResponse(kind, "The provider returned no chat message content.");
        return ParseGeneratedJson(kind, content.GetString());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, AiProviderKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderUnavailableException($"{DisplayName(kind)} request timed out.", null, "TIMEOUT", true,
                $"{DisplayName(kind)} timed out. Check the provider address and try again.", ex, DisplayName(kind));
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException($"{DisplayName(kind)} network request failed.", null,
                "NETWORK_ERROR", true,
                $"{DisplayName(kind)} could not be reached. Start it or check the provider address.", ex,
                DisplayName(kind));
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(AiProviderKind kind, HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw InvalidResponse(kind, "The provider response was not valid JSON.", ex);
        }
    }

    private static JsonDocument ParseGeneratedJson(AiProviderKind kind, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw InvalidResponse(kind, "The provider returned an empty response.");
        try { return JsonDocument.Parse(content); }
        catch (JsonException ex) { throw InvalidResponse(kind, "The model did not return valid structured JSON.", ex); }
    }

    private ProviderUnavailableException CreateFailure(AiProviderKind kind, HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        var retryable = code is 408 or 429 or 500 or 502 or 503 or 504;
        var name = DisplayName(kind);
        var userMessage = code switch
        {
            401 or 403 => $"{name} rejected the saved API key or token. Update it in Settings - AI Provider.",
            404 when kind == AiProviderKind.Ollama =>
                $"Ollama could not find model {_settings.Current.GetProvider(kind).Model}. Download it in Ollama first.",
            404 => $"{name} could not find the configured endpoint or model.",
            429 => $"{name} reached its request limit. Please try again shortly.",
            >= 500 => $"{name} is temporarily unavailable. Please try again shortly.",
            _ => $"{name} rejected the request. Check its address, model, and authentication settings."
        };
        return new ProviderUnavailableException($"{name} request failed with HTTP {code} ({response.ReasonPhrase}).", code,
            response.ReasonPhrase, retryable, userMessage, providerName: name);
    }

    private string? ResolveSecret(AiProviderKind kind, string? unsavedSecret)
    {
        if (!string.IsNullOrWhiteSpace(unsavedSecret)) return unsavedSecret.Trim();
        var stored = _secrets.Read(kind);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        return kind switch
        {
            AiProviderKind.Gemini => Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
            AiProviderKind.OpenAICompatible => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            _ => null
        };
    }

    private static void ApplyAuthentication(HttpRequestMessage request, AiProviderKind kind,
        AiProviderSettings profile, string? secret)
    {
        if (profile.Authentication == ProviderAuthentication.None) return;
        if (string.IsNullOrWhiteSpace(secret))
            throw ConfigurationFailure(kind, $"No API key or token is saved for {DisplayName(kind)}.");
        if (secret.Contains('\r') || secret.Contains('\n') || profile.AuthenticationHeader.Contains('\r') ||
            profile.AuthenticationHeader.Contains('\n') || profile.AuthenticationPrefix.Contains('\r') ||
            profile.AuthenticationPrefix.Contains('\n'))
            throw ConfigurationFailure(kind, "The authentication fields contain invalid line breaks.");
        if (profile.Authentication == ProviderAuthentication.BearerToken &&
            profile.AuthenticationHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                string.IsNullOrWhiteSpace(profile.AuthenticationPrefix) ? "Bearer" : profile.AuthenticationPrefix.Trim(),
                secret.Trim());
            return;
        }
        var value = string.IsNullOrWhiteSpace(profile.AuthenticationPrefix)
            ? secret.Trim()
            : profile.AuthenticationPrefix.Trim() + " " + secret.Trim();
        if (!request.Headers.TryAddWithoutValidation(profile.AuthenticationHeader.Trim(), value))
            throw ConfigurationFailure(kind, "The authentication header name is invalid.");
    }

    private static object[] Messages(string systemInstruction, object payload) =>
    [
        new { role = "system", content = systemInstruction },
        new { role = "user", content = JsonSerializer.Serialize(payload) }
    ];

    internal static JsonElement NormalizeSchema(object schema)
    {
        var source = JsonSerializer.SerializeToElement(schema);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteNormalized(source, writer, null);
        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static void WriteNormalized(JsonElement value, Utf8JsonWriter writer, string? propertyName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalized(property.Value, writer, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteNormalized(item, writer, propertyName);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String when propertyName == "type":
                writer.WriteStringValue(value.GetString()?.ToLowerInvariant());
                break;
            default: value.WriteTo(writer); break;
        }
    }

    private static Uri Append(Uri endpoint, string relative) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/'), UriKind.Absolute);

    private static IReadOnlyList<string> ReadModelNames(AiProviderKind kind, JsonElement root)
    {
        var values = new List<string>();
        var propertyName = kind == AiProviderKind.Ollama ? "models" : "data";
        if (!root.TryGetProperty(propertyName, out var models) || models.ValueKind != JsonValueKind.Array)
            return values;
        foreach (var model in models.EnumerateArray())
        {
            var nameProperty = kind == AiProviderKind.Ollama
                ? (model.TryGetProperty("name", out _) ? "name" : "model")
                : "id";
            if (model.TryGetProperty(nameProperty, out var value) && value.GetString() is { Length: > 0 } name)
                values.Add(name);
        }
        return values;
    }

    private static bool ModelMatches(string available, string configured) =>
        available.Equals(configured, StringComparison.OrdinalIgnoreCase) ||
        available.StartsWith(configured + ":", StringComparison.OrdinalIgnoreCase);

    private static ProviderUnavailableException ConfigurationFailure(AiProviderKind kind, string message) =>
        new(message, null, "CONFIGURATION_ERROR", false, message, providerName: DisplayName(kind));

    private static ProviderUnavailableException InvalidResponse(AiProviderKind kind, string message,
        Exception? inner = null) => new(message, null, "INVALID_RESPONSE", true,
        $"{DisplayName(kind)} returned an invalid response. Check the selected model and provider logs.", inner,
        DisplayName(kind));

    public static string DisplayName(AiProviderKind kind) => kind switch
    {
        AiProviderKind.LMStudio => "LM Studio",
        AiProviderKind.OpenAICompatible => "OpenAI-compatible provider",
        _ => kind.ToString()
    };

    public void Dispose()
    {
        _geminiClient.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
