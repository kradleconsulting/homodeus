using System.Net;
using System.Net.Http;
using System.Text;
using TelegramClaudeBot.Services;

namespace Homodeus.Tests;

public class ClaudeServiceTests
{
    private static HttpResponseMessage AnthropicResponse(string modelReplyText, HttpStatusCode status = HttpStatusCode.OK)
    {
        // Shape of a real Anthropic Messages API response, trimmed to what
        // ClaudeService actually reads (content[0].text).
        var json = $$"""
            {"content": [{"type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(modelReplyText)}}}]}
            """;
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task ClassifyAndReplyAsync_ParsesFactReply()
    {
        var factory = new FakeHttpClientFactory(_ =>
            AnthropicResponse("""{"type": "fact", "reply": "Water boils at 100C at sea level."}"""));
        var service = new ClaudeService(factory, "test-key", null);

        var result = await service.ClassifyAndReplyAsync("What's the boiling point of water?");

        Assert.Equal("fact", result.Type);
        Assert.Equal("Water boils at 100C at sea level.", result.Reply);
    }

    [Fact]
    public async Task ClassifyAndReplyAsync_ParsesJokeReply()
    {
        var factory = new FakeHttpClientFactory(_ =>
            AnthropicResponse("""{"type": "joke", "reply": "Mondays are just Saturdays with a guilt trip."}"""));
        var service = new ClaudeService(factory, "test-key", null);

        var result = await service.ClassifyAndReplyAsync("tell me a joke about mondays");

        Assert.Equal("joke", result.Type);
        Assert.Equal("Mondays are just Saturdays with a guilt trip.", result.Reply);
    }

    [Fact]
    public async Task ClassifyAndReplyAsync_IncludesHistoryTurnsBeforeNewMessage()
    {
        string? capturedBody = null;
        var factory = new FakeHttpClientFactory(req =>
        {
            // Read now, while the request (and its content) is still alive -
            // ClaudeService disposes the request right after SendAsync returns.
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return AnthropicResponse("""{"type": "fact", "reply": "About 2.1 million in Paris proper."}""");
        });
        var service = new ClaudeService(factory, "test-key", null);
        var history = new[]
        {
            new ConversationTurn("user", "What's the capital of France?"),
            new ConversationTurn("assistant", "Paris."),
        };

        await service.ClassifyAndReplyAsync("What's its population?", history);

        Assert.NotNull(capturedBody);
        using var doc = System.Text.Json.JsonDocument.Parse(capturedBody!);
        var messages = doc.RootElement.GetProperty("messages");

        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("What's the capital of France?", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("Paris.", messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("What's its population?", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ClassifyAndReplyAsync_ThrowsOnNonSuccessStatusCode()
    {
        var factory = new FakeHttpClientFactory(_ =>
            AnthropicResponse("""{"error": "rate limited"}""", HttpStatusCode.TooManyRequests));
        var service = new ClaudeService(factory, "test-key", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ClassifyAndReplyAsync("hello"));
        Assert.Contains("429", ex.Message);
    }

    [Theory]
    [InlineData("{\"type\": \"fact\", \"reply\": \"Clean JSON.\"}")]
    [InlineData("```json\n{\"type\": \"fact\", \"reply\": \"Clean JSON.\"}\n```")]
    [InlineData("```\n{\"type\": \"fact\", \"reply\": \"Clean JSON.\"}\n```")]
    public void ParseModelJson_StripsMarkdownFences(string modelOutput)
    {
        var result = ClaudeService.ParseModelJson(modelOutput);

        Assert.Equal("fact", result.Type);
        Assert.Equal("Clean JSON.", result.Reply);
    }

    [Fact]
    public void ParseModelJson_FallsBackToRawTextOnMalformedJson()
    {
        const string malformed = "not json at all";

        var result = ClaudeService.ParseModelJson(malformed);

        Assert.Equal("fact", result.Type);
        Assert.Equal(malformed, result.Reply);
    }

    [Fact]
    public void ParseModelJson_DefaultsTypeToFactWhenTypeFieldMissing()
    {
        var result = ClaudeService.ParseModelJson("""{"reply": "no type field here"}""");

        Assert.Equal("fact", result.Type);
        Assert.Equal("no type field here", result.Reply);
    }
}
