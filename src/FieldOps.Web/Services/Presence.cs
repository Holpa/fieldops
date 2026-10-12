using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace FieldOps.Web.Services;

/// <summary>
/// Who is online right now. A user can have several tabs or devices open, so presence is a count of live
/// connections per user, not a flag.
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<int, int> _connections = new();

    public event Action? Changed;

    public bool IsOnline(int userId) => _connections.TryGetValue(userId, out var count) && count > 0;

    public void Connected(int userId)
    {
        _connections.AddOrUpdate(userId, 1, (_, count) => count + 1);
        Raise(Changed);
    }

    public void Disconnected(int userId)
    {
        _connections.AddOrUpdate(userId, 0, (_, count) => Math.Max(0, count - 1));
        Raise(Changed);
    }

    // Each subscriber is isolated: a handler from a browser that just disconnected must not stop delivery to
    // everyone else, or throw back into the circuit that is connecting.
    private static void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception) { /* a torn-down subscriber; the others still get the event */ }
        }
    }
}

/// <summary>
/// Feeds <see cref="PresenceTracker"/> from the Blazor circuit lifecycle. One instance lives per circuit.
/// A circuit can drop and reconnect several times before it closes, so a flag makes sure each circuit is
/// counted at most once at any moment. An exception thrown from a circuit handler ends that user's circuit,
/// so each override logs a failure instead of letting it escape.
/// </summary>
public sealed class PresenceCircuitHandler(
    PresenceTracker presence,
    AuthenticationStateProvider authState,
    ILogger<PresenceCircuitHandler> logger) : CircuitHandler
{
    private int? _userId;
    private bool _counted;

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        try
        {
            if (_userId is null)
            {
                var state = await authState.GetAuthenticationStateAsync();
                _userId = UserContext.From(state.User)?.Id;
            }

            if (_userId is { } id && !_counted)
            {
                _counted = true;
                presence.Connected(id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Presence could not be updated when a connection came up.");
        }
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        try { Release(); }
        catch (Exception ex) { logger.LogWarning(ex, "Presence could not be updated when a connection went down."); }
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        try { Release(); }
        catch (Exception ex) { logger.LogWarning(ex, "Presence could not be updated when a circuit closed."); }
        return Task.CompletedTask;
    }

    private void Release()
    {
        if (_counted && _userId is { } id)
        {
            _counted = false;
            presence.Disconnected(id);
        }
    }
}
