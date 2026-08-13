using TelegramClaudeBot.Services;

namespace Homodeus.Tests;

public class RateLimiterServiceTests
{
    [Fact]
    public void TryAllow_AllowsUpToPerMinuteLimit_ThenDenies()
    {
        var limiter = new RateLimiterService(maxPerUserPerMinute: 3, maxTotalPerDay: 100);
        const long chatId = 1;

        Assert.True(limiter.TryAllow(chatId));
        Assert.True(limiter.TryAllow(chatId));
        Assert.True(limiter.TryAllow(chatId));
        Assert.False(limiter.TryAllow(chatId));
    }

    [Fact]
    public void TryAllow_TracksDifferentChatsIndependently()
    {
        var limiter = new RateLimiterService(maxPerUserPerMinute: 1, maxTotalPerDay: 100);

        Assert.True(limiter.TryAllow(chatOrUserId: 1));
        Assert.False(limiter.TryAllow(chatOrUserId: 1));

        // A different chat has its own per-minute window and isn't affected.
        Assert.True(limiter.TryAllow(chatOrUserId: 2));
    }

    [Fact]
    public void TryAllow_DeniesOnceDailyCapReached_AcrossAllUsers()
    {
        var limiter = new RateLimiterService(maxPerUserPerMinute: 100, maxTotalPerDay: 2);

        Assert.True(limiter.TryAllow(chatOrUserId: 1));
        Assert.True(limiter.TryAllow(chatOrUserId: 2));

        // Third message overall, from a third chat that hasn't hit its own
        // per-user cap - still denied because the global daily cap is spent.
        Assert.False(limiter.TryAllow(chatOrUserId: 3));
    }

    [Fact]
    public void TryAllow_DailyCapAppliesBeforePerUserCap()
    {
        // Daily cap is checked first; a single chat should never be able to
        // send more than the daily cap even if its own per-user cap is higher.
        var limiter = new RateLimiterService(maxPerUserPerMinute: 10, maxTotalPerDay: 1);
        const long chatId = 1;

        Assert.True(limiter.TryAllow(chatId));
        Assert.False(limiter.TryAllow(chatId));
    }
}
