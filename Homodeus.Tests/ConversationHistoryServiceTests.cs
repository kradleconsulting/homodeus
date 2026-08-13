using TelegramClaudeBot.Services;

namespace Homodeus.Tests;

public class ConversationHistoryServiceTests
{
    private sealed class TestClock
    {
        public DateTime Now { get; set; } = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public void GetTodayTurns_ReturnsEmpty_WhenChatHasNoHistory()
    {
        var history = new ConversationHistoryService(maxTurns: 8);

        Assert.Empty(history.GetTodayTurns(chatId: 1));
    }

    [Fact]
    public void AppendTurn_ThenGetTodayTurns_ReturnsThemInOrder()
    {
        var history = new ConversationHistoryService(maxTurns: 8);
        const long chatId = 1;

        history.AppendTurn(chatId, "What's the boiling point of water?", "100C at sea level.");

        var turns = history.GetTodayTurns(chatId);

        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("What's the boiling point of water?", turns[0].Text);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("100C at sea level.", turns[1].Text);
    }

    [Fact]
    public void AppendTurn_TracksDifferentChatsIndependently()
    {
        var history = new ConversationHistoryService(maxTurns: 8);

        history.AppendTurn(chatId: 1, "hello from chat 1", "hi chat 1");

        Assert.Equal(2, history.GetTodayTurns(chatId: 1).Count);
        Assert.Empty(history.GetTodayTurns(chatId: 2));
    }

    [Fact]
    public void AppendTurn_TrimsToMostRecentMaxTurns()
    {
        var history = new ConversationHistoryService(maxTurns: 2);
        const long chatId = 1;

        history.AppendTurn(chatId, "message 1", "reply 1");
        history.AppendTurn(chatId, "message 2", "reply 2");
        history.AppendTurn(chatId, "message 3", "reply 3");

        var turns = history.GetTodayTurns(chatId);

        // maxTurns=2 -> at most 4 entries (2 user+assistant pairs), oldest dropped first.
        Assert.Equal(4, turns.Count);
        Assert.Equal("message 2", turns[0].Text);
        Assert.Equal("reply 2", turns[1].Text);
        Assert.Equal("message 3", turns[2].Text);
        Assert.Equal("reply 3", turns[3].Text);
    }

    [Fact]
    public void GetTodayTurns_IsEmptyAfterUtcDateRollsOver()
    {
        var clock = new TestClock();
        var history = new ConversationHistoryService(maxTurns: 8, () => clock.Now);
        const long chatId = 1;

        history.AppendTurn(chatId, "yesterday's message", "yesterday's reply");
        Assert.NotEmpty(history.GetTodayTurns(chatId));

        clock.Now = clock.Now.AddDays(1);

        Assert.Empty(history.GetTodayTurns(chatId));
    }

    [Fact]
    public void AppendTurn_AfterDateRollover_StartsFreshInsteadOfAccumulating()
    {
        var clock = new TestClock();
        var history = new ConversationHistoryService(maxTurns: 8, () => clock.Now);
        const long chatId = 1;

        history.AppendTurn(chatId, "yesterday's message", "yesterday's reply");

        clock.Now = clock.Now.AddDays(1);
        history.AppendTurn(chatId, "today's message", "today's reply");

        var turns = history.GetTodayTurns(chatId);

        Assert.Equal(2, turns.Count);
        Assert.Equal("today's message", turns[0].Text);
        Assert.Equal("today's reply", turns[1].Text);
    }
}
