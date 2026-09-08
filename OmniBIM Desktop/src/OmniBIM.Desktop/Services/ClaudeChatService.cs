using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.Services;

/// <summary>Thrown for anything the chat panel should show as a message rather than crash on: no key configured, a network failure, an API error.</summary>
public sealed class ChatServiceException(string message) : Exception(message);

/// <summary>
/// Calls the Anthropic Messages API directly over HTTP - no SDK dependency, consistent with
/// the rest of this project.
///
/// THE API KEY NEVER LIVES IN SOURCE OR IN THIS REPO. Resolved at call time from, in order:
/// the ANTHROPIC_API_KEY environment variable (recommended - set once in Windows, works for
/// every app), then OmniBimSettings.ClaudeApiKey (a file under %LOCALAPPDATA%, outside the
/// git repo). If neither is set, SendAsync throws ChatServiceException with instructions
/// rather than silently failing or, worse, fabricating a reply.
/// </summary>
public sealed class ClaudeChatService
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private sealed class RequestBody
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("system")] public required string System { get; init; }
        [JsonPropertyName("messages")] public required List<RequestMessage> Messages { get; init; }
    }

    private sealed class RequestMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }

    private sealed class ResponseBody
    {
        [JsonPropertyName("content")] public List<ResponseBlock>? Content { get; init; }
        [JsonPropertyName("error")] public ResponseError? Error { get; init; }
    }

    private sealed class ResponseBlock
    {
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
    }

    private sealed class ResponseError
    {
        [JsonPropertyName("message")] public string? Message { get; init; }
    }

    public static string? ResolveApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var fromSettings = OmniBimSettings.Load().ClaudeApiKey;
        return string.IsNullOrWhiteSpace(fromSettings) ? null : fromSettings;
    }

    /// <summary>
    /// Sends the full conversation (system prompt + history, oldest first) and returns the
    /// assistant's reply text. Throws ChatServiceException for anything the caller should show
    /// to the user rather than treat as a bug - missing key, network failure, API error.
    /// </summary>
    public async Task<string> SendAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (apiKey is null)
        {
            throw new ChatServiceException(
                "No Claude API key configured. Set the ANTHROPIC_API_KEY environment variable " +
                "(recommended), or add one to Settings, then try again.");
        }

        var model = OmniBimSettings.Load().ClaudeModel;
        if (string.IsNullOrWhiteSpace(model)) model = "claude-sonnet-5";

        var body = new RequestBody
        {
            Model = model,
            MaxTokens = 1024,
            System = systemPrompt,
            Messages = history
                .Where(m => m.Role is ChatRole.User or ChatRole.Assistant)
                .Select(m => new RequestMessage
                {
                    Role = m.Role == ChatRole.User ? "user" : "assistant",
                    Content = m.Text,
                })
                .ToList(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ChatServiceException($"Could not reach the Claude API: {ex.Message}");
        }

        var raw = await response.Content.ReadAsStringAsync(ct);

        ResponseBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ResponseBody>(raw);
        }
        catch (JsonException)
        {
            throw new ChatServiceException($"The Claude API returned something unexpected ({(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            var apiMessage = parsed?.Error?.Message;
            throw new ChatServiceException(
                string.IsNullOrWhiteSpace(apiMessage)
                    ? $"Claude API request failed ({(int)response.StatusCode})."
                    : $"Claude API: {apiMessage}");
        }

        var text = parsed?.Content?.FirstOrDefault(b => b.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new ChatServiceException("Claude API returned an empty reply.");

        return text;
    }
}
