using FieldOps.Web.Data;
using FieldOps.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Tests;

public sealed class ChatServiceTests : IDisposable
{
    private readonly TestDbFactory _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Messaging_yourself_is_invalid()
    {
        var me = Seed.User(_db, "Avery Stone", UserRole.Office);
        var chat = new ChatService(_db, new ChatNotifier(), new PresenceTracker(),
            new FixedTimeProvider(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero)));

        var result = await chat.SendAsync(me.Id, me.Id, "Note to self");

        Assert.Equal(ResultKind.Invalid, result.Kind);
        await using var db = _db.CreateDbContext();
        Assert.False(await db.ChatMessages.AnyAsync());
    }
}
