using System.Collections.Concurrent;

namespace TelegramClaudeBot.Services;

public readonly record struct ConversationTurn(string Role, string Text);

/// <summary>
/// Simple in-memory per-chat conversation history, scoped to the current UTC
/// calendar day - a chat's history is empty again once the day rolls over.
/// Not distributed / not durable across Function restarts or multiple
/// instances - same trade-off as RateLimiterService. If you scale this up,
/// replace with Azure Table Storage keyed by (chatId, date).
/// </summary>
public class ConversationHistoryService
{
    private readonly int _maxTurns;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<long, ChatHistory> _histories = new();

    public ConversationHistoryService()
        : this(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_HISTORY_TURNS"), out var maxTurns)
                ? maxTurns : 8)
    {
    }

    // Internal ctor for tests: lets us use a small deterministic turn cap and
    // a fake clock, so day-rollover behavior can be tested without waiting
    // for or depending on the real system clock.
    internal ConversationHistoryService(int maxTurns, Func<DateTime>? utcNowProvider = null)
    {
        _maxTurns = maxTurns;
        _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Returns today's turns for this chat, oldest first. Empty if there's no
    /// history yet, or if the only history on record is from a previous day.
    /// </summary>
    public IReadOnlyList<ConversationTurn> GetTodayTurns(long chatId)
    {
        if (!_histories.TryGetValue(chatId, out var history))
            return Array.Empty<ConversationTurn>();

        lock (history.Lock)
        {
            return history.Day == _utcNow().Date
                ? history.Turns.ToArray()
                : Array.Empty<ConversationTurn>();
        }
    }

    /// <summary>
    /// Appends a user message + assistant reply pair to today's history for
    /// this chat, discarding any history from a previous day first. Trims to
    /// the most recent <c>MAX_HISTORY_TURNS</c> turns.
    /// </summary>
    public void AppendTurn(long chatId, string userText, string assistantReply)
    {
        var today = _utcNow().Date;
        var history = _histories.GetOrAdd(chatId, static _ => new ChatHistory());

        lock (history.Lock)
        {
            if (history.Day != today)
            {
                history.Day = today;
                history.Turns.Clear();
            }

            history.Turns.Add(new ConversationTurn("user", userText));
            history.Turns.Add(new ConversationTurn("assistant", assistantReply));

            // A "turn" is one user+assistant pair, so cap at _maxTurns * 2 entries.
            var maxEntries = Math.Max(0, _maxTurns) * 2;
            while (history.Turns.Count > maxEntries)
                history.Turns.RemoveAt(0);
        }
    }

    private sealed class ChatHistory
    {
        public readonly object Lock = new();
        public readonly List<ConversationTurn> Turns = new();
        public DateTime Day = DateTime.MinValue;
    }
}
