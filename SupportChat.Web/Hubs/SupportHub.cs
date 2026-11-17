using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SupportChat.Web.Models;
using SupportChat.Web.Services;

namespace SupportChat.Web.Hubs;

/// <summary>Methods the server can call on connected browsers.</summary>
public interface ISupportClient
{
    Task ReceiveMessage(MessageDto message);
    Task TicketUpdated(TicketSummaryDto ticket);
    Task TicketCreated(TicketSummaryDto ticket);
    Task Notification(NotificationDto notification);
    Task UserTyping(int ticketId, string userName, bool isTyping);
    Task PresenceChanged(string userId, bool isOnline);
}

/// <summary>Real-time hub: ticket chat rooms, typing indicators, presence and notifications.</summary>
[Authorize]
public class SupportHub : Hub<ISupportClient>
{
    public const string StaffGroup = "staff";
    public static string TicketGroup(int ticketId) => $"ticket-{ticketId}";

    private readonly PresenceTracker _presence;
    private readonly ITicketService _tickets;
    private readonly IChatService _chat;

    public SupportHub(PresenceTracker presence, ITicketService tickets, IChatService chat)
    {
        _presence = presence;
        _tickets = tickets;
        _chat = chat;
    }

    private CurrentUser Me => Context.User!.ToCurrentUser();

    public override async Task OnConnectedAsync()
    {
        var me = Me;
        if (me.IsStaff) await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup);

        if (_presence.UserConnected(me.Id, Context.ConnectionId))
            await Clients.All.PresenceChanged(me.Id, true);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var me = Me;
        if (_presence.UserDisconnected(me.Id, Context.ConnectionId))
            await Clients.All.PresenceChanged(me.Id, false);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Join the chat room of a ticket (access is verified).</summary>
    public async Task JoinTicket(int ticketId)
    {
        if (!await _tickets.CanAccessAsync(ticketId, Me))
            throw new HubException("You do not have access to this ticket.");
        await Groups.AddToGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
    }

    public Task LeaveTicket(int ticketId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, TicketGroup(ticketId));

    /// <summary>Send a text message. (Messages with files are uploaded over HTTP and broadcast the same way.)</summary>
    public async Task<MessageDto> SendMessage(int ticketId, string content)
    {
        try
        {
            return await _chat.SendMessageAsync(ticketId, Me, content, null);
        }
        catch (SupportException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task Typing(int ticketId, bool isTyping) =>
        Clients.OthersInGroup(TicketGroup(ticketId)).UserTyping(ticketId, Me.FullName, isTyping);
}

/// <summary>Tracks which users are online (a user can have several tabs / connections).</summary>
public class PresenceTracker
{
    private readonly Dictionary<string, HashSet<string>> _connections = new();
    private readonly object _lock = new();

    /// <returns>true when this is the user's first connection (user came online).</returns>
    public bool UserConnected(string userId, string connectionId)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(userId, out var set))
            {
                set.Add(connectionId);
                return false;
            }
            _connections[userId] = new HashSet<string> { connectionId };
            return true;
        }
    }

    /// <returns>true when the user's last connection closed (user went offline).</returns>
    public bool UserDisconnected(string userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set)) return false;
            set.Remove(connectionId);
            if (set.Count > 0) return false;
            _connections.Remove(userId);
            return true;
        }
    }

    public bool IsOnline(string? userId)
    {
        if (userId is null) return false;
        lock (_lock) return _connections.ContainsKey(userId);
    }
}

/// <summary>Pushes events to browsers through SignalR.</summary>
public interface IRealtimeNotifier
{
    Task MessageSentAsync(MessageDto message);
    Task TicketUpdatedAsync(TicketSummaryDto ticket);
    Task TicketCreatedAsync(TicketSummaryDto ticket);
    Task NotifyUserAsync(string userId, NotificationDto notification);
}

public class SignalRNotifier : IRealtimeNotifier
{
    private readonly IHubContext<SupportHub, ISupportClient> _hub;

    public SignalRNotifier(IHubContext<SupportHub, ISupportClient> hub) => _hub = hub;

    public Task MessageSentAsync(MessageDto message) =>
        _hub.Clients.Group(SupportHub.TicketGroup(message.TicketId)).ReceiveMessage(message);

    public Task TicketUpdatedAsync(TicketSummaryDto ticket) => Task.WhenAll(
        _hub.Clients.Group(SupportHub.TicketGroup(ticket.Id)).TicketUpdated(ticket),
        _hub.Clients.Group(SupportHub.StaffGroup).TicketUpdated(ticket),
        _hub.Clients.User(ticket.CustomerId).TicketUpdated(ticket));

    public Task TicketCreatedAsync(TicketSummaryDto ticket) =>
        _hub.Clients.Group(SupportHub.StaffGroup).TicketCreated(ticket);

    public Task NotifyUserAsync(string userId, NotificationDto notification) =>
        _hub.Clients.User(userId).Notification(notification);
}
