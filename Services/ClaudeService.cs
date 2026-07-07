using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TelegramClaudeBot.Services;

public record ClaudeReply(string Type, string Reply);

public class ClaudeService
{
    private const string AnthropicVersion = "2023-06-01";
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;

    private const string SystemPrompt = """
        You are the reply engine for a Telegram channel bot.
        For every incoming message, decide whether it is:
        - "fact": a genuine request for objective / factual information, OR
        - "joke": a message that is playful, silly, sarcastic, or clearly inviting humor.

        Respond ONLY with a single JSON object, no other text, no markdown fences, in this exact shape:
        {"type": "fact" | "joke", "reply": "your reply text here"}

        Rules:
        - If "fact": give a short, accurate, and directly useful answer (2-4 sentences max).
        - If "joke": reply with a short, original, good-natured joke or witty one-liner relevant to the message. Never punch down, never use slurs or offensive humor.
        - Keep all replies under 400 characters.
        - If you are unsure which type it is, default to "fact" and just answer helpfully.
        """;

    public ClaudeService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
        _model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-haiku-4-5-20251001";
    }

    public async Task<ClaudeReply> ClassifyAndReplyAsync(string userMessage, CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = _model,
            max_tokens = 300,
            system = SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Covers 429 (rate limit / spend cap hit), 4xx, 5xx from Anthropic.
            throw new InvalidOperationException($"Anthropic API error {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        return ParseModelJson(text);
    }

    private static ClaudeReply ParseModelJson(string text)
    {
        try
        {
            // Defensive: strip accidental markdown fences if the model adds them.
            var cleaned = text.Trim().Trim('`').Trim();
            if (cleaned.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[4..].Trim();

            using var doc = JsonDocument.Parse(cleaned);
            var type = doc.RootElement.GetProperty("type").GetString() ?? "fact";
            var reply = doc.RootElement.GetProperty("reply").GetString() ?? "";
            return new ClaudeReply(type, reply);
        }
        catch (JsonException)
        {
            // Fallback: if parsing fails for any reason, just send the raw text back.
            return new ClaudeReply("fact", text);
        }
    }
}
