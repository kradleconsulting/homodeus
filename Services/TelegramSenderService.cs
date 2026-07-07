using Telegram.Bot;

namespace TelegramClaudeBot.Services;

public class TelegramSenderService
{
    private readonly TelegramBotClient _client;

    public TelegramSenderService()
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
            ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set.");
        _client = new TelegramBotClient(token);
    }

    // NOTE: Telegram.Bot has renamed this method across major versions
    // (SendTextMessageAsync in older versions, SendMessage in newer ones).
    // If this doesn't compile against the version NuGet restores, check
    // IntelliSense/autocomplete on _client. for the current name - it's a
    // one-line fix either way.
    public Task SendAsync(long chatId, string text, CancellationToken ct = default)
        => _client.SendMessage(chatId, text, cancellationToken: ct);
}
