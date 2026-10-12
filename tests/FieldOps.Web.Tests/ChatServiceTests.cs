using FieldOps.Web.Data;
using FieldOps.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldOps.Web.Tests;

public sealed class ChatServiceTests : IDisposable
{
    private readonly TestDbFactory _db = new();
    private readonly ChatSendLimiter _sendLimiter = new(NullLoggerFactory.Instance);

    public void Dispose()
    {
        _sendLimiter.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Messaging_yourself_is_invalid()
    {
        var me = Seed.User(_db, "Avery Stone", UserRole.Office);
        var chat = new ChatService(_db, new ChatNotifier(), new PresenceTracker(), _sendLimiter,
            new FixedTimeProvider(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero)));

        var result = await chat.SendAsync(me.Id, me.Id, "Note to self");

        Assert.Equal(ResultKind.Invalid, result.Kind);
        await using var db = _db.CreateDbContext();
        Assert.False(await db.ChatMessages.AnyAsync());
    }

    [Fact]
    public async Task A_message_over_the_per_minute_limit_is_refused_and_not_stored()
    {
        var me = Seed.User(_db, "Avery Stone", UserRole.Office);
        var other = Seed.User(_db, "Kai Moreno", UserRole.Field);
        var chat = new ChatService(_db, new ChatNotifier(), new PresenceTracker(), _sendLimiter,
            new FixedTimeProvider(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero)));

        for (var i = 1; i <= ChatSendLimiter.PerMinute; i++)
            Assert.True((await chat.SendAsync(me.Id, other.Id, "Message " + i)).IsOk);
        var refused = await chat.SendAsync(me.Id, other.Id, "One too many");

        Assert.Equal(ResultKind.Invalid, refused.Kind);
        await using var db = _db.CreateDbContext();
        Assert.Equal(ChatSendLimiter.PerMinute, await db.ChatMessages.CountAsync());
        Assert.False(await db.ChatMessages.AnyAsync(m => m.Body == "One too many"));
    }
}
