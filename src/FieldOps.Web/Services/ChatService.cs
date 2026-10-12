using System.Threading.RateLimiting;
using FieldOps.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Services;

public sealed record ChatContact(int UserId, string Name, string Title, int Unread, DateTime? LastMessageUtc, bool IsOnline);

public sealed record ChatLine(int Id, int FromId, string Body, DateTime SentUtc, bool IsDone, string? DoneByName, DateTime? DoneUtc);

/// <summary>One-to-one team chat. The sender is always the signed-in user, never a value the client supplies.</summary>
public sealed class ChatService(
    IDbContextFactory<AppDbContext> dbFactory,
    ChatNotifier notifier,
    PresenceTracker presence,
    ChatSendLimiter sendLimiter,
    TimeProvider clock)
{
    public const int MaxLength = 2000;

    public async Task<IReadOnlyList<ChatContact>> ContactsAsync(int me, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => u.Id != me).OrderBy(u => u.Name).ToListAsync(ct);

        var unread = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ToId == me && m.ReadUtc == null)
            .GroupBy(m => m.FromId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var lastSent = await db.ChatMessages.AsNoTracking()
            .Where(m => m.FromId == me || m.ToId == me)
            .GroupBy(m => m.FromId == me ? m.ToId : m.FromId)
            .Select(g => new { UserId = g.Key, Last = g.Max(m => m.SentUtc) })
            .ToDictionaryAsync(x => x.UserId, x => x.Last, ct);

        return users
            .Select(u => new ChatContact(u.Id, u.Name, u.Title, unread.GetValueOrDefault(u.Id),
                lastSent.TryGetValue(u.Id, out var last) ? last : null, presence.IsOnline(u.Id)))
            .OrderByDescending(c => c.Unread > 0)
            .ThenByDescending(c => c.LastMessageUtc)
            .ThenBy(c => c.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<ChatLine>> ConversationAsync(int me, int other, int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        // Newest `take` messages, returned oldest-first for reading order.
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => (m.FromId == me && m.ToId == other) || (m.FromId == other && m.ToId == me))
            .OrderByDescending(m => m.SentUtc).ThenByDescending(m => m.Id)
            .Take(take)
            .ToListAsync(ct);

        return messages
            .OrderBy(m => m.SentUtc).ThenBy(m => m.Id)
            .Select(m => new ChatLine(m.Id, m.FromId, m.Body, m.SentUtc, m.DoneUtc is not null,
                m.DoneById is { } by ? names.GetValueOrDefault(by) : null, m.DoneUtc))
            .ToList();
    }

    public async Task<ServiceResult> SendAsync(int me, int to, string body, CancellationToken ct = default)
    {
        body = body.Trim();
        if (body.Length == 0) return ServiceResult.Invalid("Type a message first.");
        if (body.Length > MaxLength) return ServiceResult.Invalid($"Messages are limited to {MaxLength} characters.");
        if (to == me) return ServiceResult.Invalid("You can't message yourself.");
        if (!sendLimiter.TryAcquire(me)) return ServiceResult.Invalid("You're sending messages too quickly. Wait a moment and try again.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Users.AnyAsync(u => u.Id == to, ct)) return ServiceResult.NotFound("That person no longer exists.");

        db.ChatMessages.Add(new ChatMessage { FromId = me, ToId = to, Body = body, SentUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);

        notifier.NotifySent(me, to);
        return ServiceResult.Ok();
    }

    /// <summary>Marks the other person's messages to me as read. Reading never notifies the sender.</summary>
    public async Task MarkReadAsync(int me, int other, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        int updated = await db.ChatMessages
            .Where(m => m.FromId == other && m.ToId == me && m.ReadUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.ReadUtc, clock.GetUtcNow().UtcDateTime), ct);

        if (updated > 0) notifier.NotifyRead(me);
    }

    /// <summary>
    /// Marks a message as handled, or reopens it. Either participant may do either; anyone else gets the same
    /// answer as for a message that does not exist, so message ids cannot be probed.
    /// </summary>
    public async Task<ServiceResult> SetDoneAsync(int me, int messageId, bool done, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var message = await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId && (m.FromId == me || m.ToId == me), ct);
        if (message is null) return ServiceResult.NotFound("That message isn't in one of your conversations.");
        if ((message.DoneUtc is not null) == done) return ServiceResult.Ok();

        message.DoneUtc = done ? clock.GetUtcNow().UtcDateTime : null;
        message.DoneById = done ? me : null;
        await db.SaveChangesAsync(ct);

        notifier.NotifyUpdated(message.FromId, message.ToId);
        return ServiceResult.Ok();
    }

    public async Task<int> UnreadCountAsync(int me, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ChatMessages.CountAsync(m => m.ToId == me && m.ReadUtc == null, ct);
    }
}

/// <summary>
/// Flood brake for team chat: each user may send 20 messages a minute. Chat sends travel over the Blazor
/// circuit, not HTTP, so the request rate limiter never sees them. One instance is shared by the whole app.
/// </summary>
public sealed class ChatSendLimiter(ILoggerFactory loggerFactory) : IDisposable
{
    public const int PerMinute = 20;

    private readonly ILogger _security = SecurityLog.For(loggerFactory);

    private readonly PartitionedRateLimiter<int> _limiter = PartitionedRateLimiter.Create<int, int>(userId =>
        RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    public bool TryAcquire(int userId)
    {
        using var lease = _limiter.AttemptAcquire(userId);
        if (!lease.IsAcquired) _security.ChatRateLimited(userId);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
