using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramClaudeBot.Services;

namespace TelegramClaudeBot.Functions;

public class TelegramWebhookFunction
{
    private readonly ILogger _logger;
    private readonly RateLimiterService _rateLimiter;
    private readonly ClaudeService _claude;
    private readonly TelegramSenderService _sender;
    private readonly int _maxInputLength;

    public TelegramWebhookFunction(
        ILoggerFactory loggerFactory,
        RateLimiterService rateLimiter,
        ClaudeService claude,
        TelegramSenderService sender)
    {
        _logger = loggerFactory.CreateLogger<TelegramWebhookFunction>();
        _rateLimiter = rateLimiter;
        _claude = claude;
        _sender = sender;
        _maxInputLength = int.TryParse(
            Environment.GetEnvironmentVariable("MAX_INPUT_MESSAGE_LENGTH"), out var len) ? len : 500;
    }

    [Function("TelegramWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "telegram/webhook")] HttpRequestData req,
        CancellationToken ct)
    {
        // Always return 200 quickly to Telegram, even on internal errors,
        // so Telegram doesn't retry-storm you. Log failures instead.
        var response = req.CreateResponse(HttpStatusCode.OK);

        Update? update;
        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync(ct);
            update = JsonSerializer.Deserialize<Update>(body, JsonBotAPI.Options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse incoming Telegram update.");
            return response;
        }

        // Channel posts arrive as update.ChannelPost, not update.Message.
        // Regular chats/groups use Message; channels use ChannelPost.
        var message = update?.Message ?? update?.ChannelPost;
        if (message?.Text is not { Length: > 0 } text || message.Chat is null)
        {
            // Non-text message (photo, sticker, etc.), or neither field populated.
            _logger.LogInformation("Update had no usable text: {Update}",
                JsonSerializer.Serialize(update));
            return response;
        }

        var chatId = message.Chat.Id;

        if (text.Length > _maxInputLength)
        {
            await SafeSend(chatId, "That message is a bit long for me to process - try something shorter!", ct);
            return response;
        }

        if (!_rateLimiter.TryAllow(chatId))
        {
            _logger.LogInformation("Rate limit hit for chat {ChatId}", chatId);
            // Stay quiet rather than spamming a "slow down" message on every hit.
            return response;
        }

        try
        {
            var result = await _claude.ClassifyAndReplyAsync(text, ct);
            await SafeSend(chatId, result.Reply, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Claude API for chat {ChatId}", chatId);
            // Don't leak internals to the channel; fail silently or with a generic note.
        }

        return response;
    }

    private async Task SafeSend(long chatId, string text, CancellationToken ct)
    {
        try
        {
            await _sender.SendAsync(chatId, text, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to chat {ChatId}", chatId);
        }
    }
}
