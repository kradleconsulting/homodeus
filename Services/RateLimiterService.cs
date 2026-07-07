using System.Collections.Concurrent;

namespace TelegramClaudeBot.Services;

/// <summary>
/// Simple in-memory rate limiter. Not distributed / not durable across
/// Function restarts or multiple instances - fine for a hobby-scale bot.
/// If you scale this up, replace with Azure Table Storage or Redis counters.
/// </summary>
public class RateLimiterService
{
    private readonly int _maxPerUserPerMinute;
    private readonly int _maxTotalPerDay;

    private readonly ConcurrentDictionary<long, Queue<DateTime>> _userHits = new();
    private int _dailyCount;
    private DateTime _dailyWindowStart = DateTime.UtcNow.Date;
    private readonly object _dailyLock = new();

    public RateLimiterService()
    {
        _maxPerUserPerMinute = int.TryParse(
            Environment.GetEnvironmentVariable("MAX_MESSAGES_PER_USER_PER_MINUTE"), out var perUser)
            ? perUser : 5;

        _maxTotalPerDay = int.TryParse(
            Environment.GetEnvironmentVariable("MAX_MESSAGES_PER_DAY_TOTAL"), out var perDay)
            ? perDay : 500;
    }

    /// <summary>
    /// Returns true if this message is allowed to proceed to the Claude API call.
    /// </summary>
    public bool TryAllow(long chatOrUserId)
    {
        if (!CheckAndIncrementDailyCap())
            return false;

        var now = DateTime.UtcNow;
        var queue = _userHits.GetOrAdd(chatOrUserId, _ => new Queue<DateTime>());

        lock (queue)
        {
            while (queue.Count > 0 && (now - queue.Peek()).TotalSeconds > 60)
                queue.Dequeue();

            if (queue.Count >= _maxPerUserPerMinute)
                return false;

            queue.Enqueue(now);
            return true;
        }
    }

    private bool CheckAndIncrementDailyCap()
    {
        lock (_dailyLock)
        {
            var today = DateTime.UtcNow.Date;
            if (today != _dailyWindowStart)
            {
                _dailyWindowStart = today;
                _dailyCount = 0;
            }

            if (_dailyCount >= _maxTotalPerDay)
                return false;

            _dailyCount++;
            return true;
        }
    }
}
