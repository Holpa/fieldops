namespace FieldOps.Web.Services;

/// <summary>
/// Cross-circuit signals for team chat. Registered as a singleton, so every connected browser hears them
/// and refreshes only when the event involves its own user.
/// <para>
/// The three events are kept apart on purpose. A message being marked done is not a new message and does not
/// change anyone's unread count, so it travels on <see cref="MessageUpdated"/>, which the unread badge never
/// listens to.
/// </para>
/// </summary>
public sealed class ChatNotifier
{
    /// <summary>A new message from <c>fromId</c> to <c>toId</c>.</summary>
    public event Action<int, int>? MessageSent;

    /// <summary>A message between the two users was marked done or reopened.</summary>
    public event Action<int, int>? MessageUpdated;

    /// <summary>The unread count of the given user may have changed.</summary>
    public event Action<int>? UnreadChanged;

    public void NotifySent(int fromId, int toId)
    {
        Raise(MessageSent, fromId, toId);
        Raise(UnreadChanged, toId);
    }

    public void NotifyUpdated(int userA, int userB) => Raise(MessageUpdated, userA, userB);

    public void NotifyRead(int userId) => Raise(UnreadChanged, userId);

    // Each subscriber is isolated: a handler from a browser that just disconnected must not stop delivery to
    // everyone else, or throw back into the circuit of the person who sent the message.
    private static void Raise(Action<int, int>? handlers, int a, int b)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<int, int>>())
        {
            try { handler(a, b); }
            catch (Exception) { /* a torn-down subscriber; the others still get the event */ }
        }
    }

    private static void Raise(Action<int>? handlers, int userId)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<int>>())
        {
            try { handler(userId); }
            catch (Exception) { /* a torn-down subscriber; the others still get the event */ }
        }
    }
}
