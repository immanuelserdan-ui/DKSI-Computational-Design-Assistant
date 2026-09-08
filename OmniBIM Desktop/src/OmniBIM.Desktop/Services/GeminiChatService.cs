using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.Services;

/// <summary>Thrown for anything the chat panel should show as a message rather than crash on: no key configured, a network failure, an API error.</summary>
public sealed class ChatServiceException(string message) : Exception(message);

/// <summary>
/// Calls Google's Generative Language API (Gemini) directly over HTTP - no SDK dependency,
/// consistent with the rest of this project. Chosen over a paid provider because Google AI
/// Studio issues API keys with a genuinely free quota (see https://aistudio.google.com/apikey);
/// swap the endpoint/request shape here if that trade-off ever changes.
///
/// THE API KEY NEVER LIVES IN SOURCE OR IN THIS REPO. Resolved at call time from, in order:
/// the GEMINI_API_KEY environment variable (recommended - set once in Windows, works for
/// every app), then OmniBimSettings.GeminiApiKey (a file under %LOCALAPPDATA%, outside the
/// git repo). If neither is set, SendAsync throws ChatServiceException with instructions
/// rather than silently failing or, worse, fabricating a reply.
/// </summary>
public sealed class GeminiChatService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private sealed class Part
    {
        [JsonPropertyName("text")] public required string Text { get; init; }
    }

    private sealed class Content
    {
        [JsonPropertyName("role")] public string? Role { get; init; }
        [JsonPropertyName("parts")] public required List<Part> Parts { get; init; }
    }

    private sealed class RequestBody
    {
        [JsonPropertyName("system_instruction")] public required Content SystemInstruction { get; init; }
        [JsonPropertyName("contents")] public required List<Content> Contents { get; init; }
    }

    private sealed class ResponseCandidate
    {
        [JsonPropertyName("content")] public Content? Content { get; init; }
    }

    private sealed class ResponseBody
    {
        [JsonPropertyName("candidates")] public List<ResponseCandidate>? Candidates { get; init; }
        [JsonPropertyName("error")] public ResponseError? Error { get; init; }
    }

    private sealed class ResponseError
    {
        [JsonPropertyName("message")] public string? Message { get; init; }
    }

    public static string? ResolveApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var fromSettings = OmniBimSettings.Load().GeminiApiKey;
        return string.IsNullOrWhiteSpace(fromSettings) ? null : fromSettings;
    }

    /// <summary>
    /// Sends the full conversation (system prompt + history, oldest first) and returns the
    /// model's reply text. Throws ChatServiceException for anything the caller should show to
    /// the user rather than treat as a bug - missing key, network failure, API error.
    /// </summary>
    public async Task<string> SendAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (apiKey is null)
        {
            throw new ChatServiceException(
                "No Gemini API key configured. Get a free one at https://aistudio.google.com/apikey, " +
                "set the GEMINI_API_KEY environment variable (recommended) or add one to Settings, then try again.");
        }

        var model = OmniBimSettings.Load().GeminiModel;
        if (string.IsNullOrWhiteSpace(model)) model = "gemini-2.0-flash";

        var body = new RequestBody
        {
            SystemInstruction = new Content { Parts = [new Part { Text = systemPrompt }] },
            // Gemini uses "model" for the assistant's own turns, not "assistant".
            Contents = history
                .Where(m => m.Role is ChatRole.User or ChatRole.Assistant)
                .Select(m => new Content
                {
                    Role = m.Role == ChatRole.User ? "user" : "model",
                    Parts = [new Part { Text = m.Text }],
                })
                .ToList(),
        };

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ChatServiceException($"Could not reach the Gemini API: {ex.Message}");
        }

        var raw = await response.Content.ReadAsStringAsync(ct);

        ResponseBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ResponseBody>(raw);
        }
        catch (JsonException)
        {
            throw new ChatServiceException($"The Gemini API returned something unexpected ({(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            var apiMessage = parsed?.Error?.Message;
            throw new ChatServiceException(
                string.IsNullOrWhiteSpace(apiMessage)
                    ? $"Gemini API request failed ({(int)response.StatusCode})."
                    : $"Gemini API: {apiMessage}");
        }

        var text = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new ChatServiceException("Gemini API returned an empty reply - the prompt may have been blocked by a safety filter.");

        return text;
    }
}
