using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportChat.Web.Data;
using SupportChat.Web.Helpers;
using SupportChat.Web.Hubs;
using SupportChat.Web.Models;
using SupportChat.Web.ViewModels;

namespace SupportChat.Web.Services;

// =====================================================================
//  FILE STORAGE
// =====================================================================

public class StorageOptions
{
    public string UploadPath { get; set; } = "App_Data/uploads";
    public int MaxFileSizeMB { get; set; } = 10;
    public int MaxFilesPerMessage { get; set; } = 5;
}

public record StoredFile(string OriginalName, string StoredName, string ContentType, long Size);

public interface IFileStorageService
{
    void Validate(IReadOnlyList<IFormFile> files);
    Task<StoredFile> SaveAsync(IFormFile file, CancellationToken ct = default);
    string GetFullPath(string storedName);
}

/// <summary>Stores attachments outside wwwroot – they are only served through an authorized controller.</summary>
public class FileStorageService : IFileStorageService
{
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt", ".csv", ".zip", ".log"
    };

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly StorageOptions _options;
    private readonly string _root;

    public FileStorageService(IOptions<StorageOptions> options, IWebHostEnvironment env)
    {
        _options = options.Value;
        _root = Path.Combine(env.ContentRootPath, _options.UploadPath);
        Directory.CreateDirectory(_root);
    }

    public void Validate(IReadOnlyList<IFormFile> files)
    {
        if (files.Count > _options.MaxFilesPerMessage)
            throw new SupportException($"You can attach up to {_options.MaxFilesPerMessage} files per message.");

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(ext))
                throw new SupportException($"'{file.FileName}' is not an allowed file type.");
            if (file.Length == 0)
                throw new SupportException($"'{file.FileName}' is empty.");
            if (file.Length > _options.MaxFileSizeMB * 1024L * 1024L)
                throw new SupportException($"'{file.FileName}' is larger than {_options.MaxFileSizeMB} MB.");
        }
    }

    public async Task<StoredFile> SaveAsync(IFormFile file, CancellationToken ct = default)
    {
        var original = Path.GetFileName(file.FileName);
        if (original.Length > 200) original = original[^200..];

        var ext = Path.GetExtension(original).ToLowerInvariant();
        var stored = $"{Guid.NewGuid():N}{ext}";

        await using (var stream = File.Create(Path.Combine(_root, stored)))
            await file.CopyToAsync(stream, ct);

        var contentType = ContentTypes.TryGetContentType(original, out var type) ? type : "application/octet-stream";
        return new StoredFile(original, stored, contentType, file.Length);
    }

    public string GetFullPath(string storedName) => Path.Combine(_root, Path.GetFileName(storedName));
}

// =====================================================================
//  NOTIFICATIONS
// =====================================================================

public interface INotificationService
{
    Task NotifyAsync(string userId, NotificationType type, string title, string body, string? link);
    Task<(List<NotificationDto> Items, int Unread)> GetRecentAsync(string userId, int take = 15);
    Task MarkAllReadAsync(string userId);
    Task<string?> OpenAsync(int id, string userId);
}

public class NotificationService : INotificationService
{
    private readonly AppDbContext _db;
    private readonly IRealtimeNotifier _realtime;

    public NotificationService(AppDbContext db, IRealtimeNotifier realtime)
    {
        _db = db;
        _realtime = realtime;
    }

    public async Task NotifyAsync(string userId, NotificationType type, string title, string body, string? link)
    {
        var notification = new Notification
        {
            UserId = userId,
            Type = type,
            Title = Trim(title, 150),
            Body = Trim(body, 300),
            Link = link
        };
        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();
        await _realtime.NotifyUserAsync(userId, ToDto(notification));
    }

    public async Task<(List<NotificationDto> Items, int Unread)> GetRecentAsync(string userId, int take = 15)
    {
        var items = await _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.Id)
            .Take(take)
            .ToListAsync();
        var unread = await _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
        return (items.Select(ToDto).ToList(), unread);
    }

    public Task MarkAllReadAsync(string userId) =>
        _db.Notifications.Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

    public async Task<string?> OpenAsync(int id, string userId)
    {
        var notification = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (notification is null) return null;
        notification.IsRead = true;
        await _db.SaveChangesAsync();
        return notification.Link;
    }

    private static NotificationDto ToDto(Notification n) => new()
    {
        Id = n.Id,
        Type = n.Type.ToString(),
        Title = n.Title,
        Body = n.Body,
        Link = n.Link,
        IsRead = n.IsRead,
        CreatedAt = UI.UtcIso(n.CreatedAt)
    };

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

// =====================================================================
//  TICKETS
// =====================================================================

public interface ITicketService
{
    Task<Ticket> CreateAsync(CurrentUser user, CreateTicketViewModel model, CancellationToken ct = default);
    Task<TicketListViewModel> GetListAsync(CurrentUser user, TicketFilter filter);
    Task<TicketDetailsViewModel?> GetDetailsAsync(int id, CurrentUser user);
    Task<bool> CanAccessAsync(int ticketId, CurrentUser user);
    Task UpdateStatusAsync(int id, TicketStatus status, CurrentUser user);
    Task AssignAsync(int id, string? agentId, CurrentUser user);
    Task<DashboardViewModel> GetDashboardAsync(CurrentUser user);
    Task<List<AgentOption>> GetAgentsAsync();
    Task<TicketSummaryDto> GetSummaryAsync(int id);
}

public class TicketService : ITicketService
{
    private const int PageSize = 10;

    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IChatService _chat;
    private readonly INotificationService _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly PresenceTracker _presence;

    public TicketService(AppDbContext db, UserManager<ApplicationUser> users, IChatService chat,
        INotificationService notifications, IRealtimeNotifier realtime, PresenceTracker presence)
    {
        _db = db;
        _users = users;
        _chat = chat;
        _notifications = notifications;
        _realtime = realtime;
        _presence = presence;
    }

    public async Task<Ticket> CreateAsync(CurrentUser user, CreateTicketViewModel model, CancellationToken ct = default)
    {
        var files = model.Attachments ?? new List<IFormFile>();
        var ticket = new Ticket
        {
            Subject = model.Subject.Trim(),
            Description = model.Description.Trim(),
            Category = model.Category,
            Priority = model.Priority,
            Status = TicketStatus.Open,
            CustomerId = user.Id
        };

        // The description becomes the first chat message (with any attachments)
        var firstMessage = new Message { SenderId = user.Id, Content = ticket.Description };
        await _chat.AttachFilesAsync(firstMessage, files, ct);
        ticket.Messages.Add(firstMessage);

        // Automatic agent assignment (online agents first, then lowest workload)
        var agentId = await PickAgentAsync();
        if (agentId is not null)
        {
            ticket.AssignedAgentId = agentId;
            var agent = await _users.FindByIdAsync(agentId);
            ticket.Messages.Add(new Message
            {
                SenderId = user.Id,
                IsSystemMessage = true,
                Content = $"Ticket automatically assigned to {agent?.FullName}."
            });
        }

        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync(ct);
        ticket.TicketNumber = $"TKT-{ticket.Id:D5}";
        await _db.SaveChangesAsync(ct);

        var link = $"/Tickets/Details/{ticket.Id}";
        if (agentId is not null)
            await _notifications.NotifyAsync(agentId, NotificationType.TicketAssigned,
                $"New ticket assigned: {ticket.TicketNumber}", $"{user.FullName}: {ticket.Subject}", link);

        await _realtime.TicketCreatedAsync(await GetSummaryAsync(ticket.Id));
        return ticket;
    }

    public async Task<TicketListViewModel> GetListAsync(CurrentUser user, TicketFilter f)
    {
        var query = _db.Tickets.AsNoTracking().AsQueryable();

        if (user.IsCustomer)
        {
            query = query.Where(t => t.CustomerId == user.Id);
            f.Scope = null;
        }
        else
        {
            f.Scope = string.IsNullOrWhiteSpace(f.Scope) ? (user.IsAgent ? "mine" : "all") : f.Scope.ToLowerInvariant();
            if (f.Scope == "mine") query = query.Where(t => t.AssignedAgentId == user.Id);
            else if (f.Scope == "unassigned") query = query.Where(t => t.AssignedAgentId == null);
        }

        if (f.Status.HasValue) { var s = f.Status.Value; query = query.Where(t => t.Status == s); }
        if (f.Priority.HasValue) { var p = f.Priority.Value; query = query.Where(t => t.Priority == p); }
        if (f.Category.HasValue) { var c = f.Category.Value; query = query.Where(t => t.Category == c); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            query = query.Where(t => t.Subject.Contains(term) || t.TicketNumber.Contains(term) || t.Customer.FullName.Contains(term));
        }

        var total = await query.CountAsync();
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        f.Page = Math.Clamp(f.Page, 1, pages);

        var items = await Project(query.OrderByDescending(t => t.LastActivityAt))
            .Skip((f.Page - 1) * PageSize).Take(PageSize).ToListAsync();

        return new TicketListViewModel { Filter = f, Items = items, TotalCount = total, TotalPages = pages, User = user };
    }

    public async Task<TicketDetailsViewModel?> GetDetailsAsync(int id, CurrentUser user)
    {
        var ticket = await _db.Tickets.AsNoTracking()
            .Include(t => t.Customer)
            .Include(t => t.AssignedAgent)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket is null || !CanAccess(ticket, user)) return null;

        return new TicketDetailsViewModel
        {
            Ticket = ticket,
            User = user,
            Messages = await _chat.GetMessagesAsync(id),
            Agents = user.IsStaff ? await GetAgentsAsync() : new List<AgentOption>(),
            CustomerOnline = _presence.IsOnline(ticket.CustomerId),
            AgentOnline = _presence.IsOnline(ticket.AssignedAgentId)
        };
    }

    public async Task<bool> CanAccessAsync(int ticketId, CurrentUser user) =>
        user.IsStaff
            ? await _db.Tickets.AnyAsync(t => t.Id == ticketId)
            : await _db.Tickets.AnyAsync(t => t.Id == ticketId && t.CustomerId == user.Id);

    public async Task UpdateStatusAsync(int id, TicketStatus status, CurrentUser user)
    {
        var ticket = await _db.Tickets.Include(t => t.Customer).FirstOrDefaultAsync(t => t.Id == id)
                     ?? throw new SupportException("Ticket not found.", 404);
        if (!CanAccess(ticket, user)) throw new SupportException("Access denied.", 403);
        if (ticket.Status == status) throw new SupportException($"Ticket is already '{UI.StatusLabel(status)}'.");

        if (user.IsCustomer)
        {
            var allowed = status == TicketStatus.Closed
                          || (status == TicketStatus.Open && ticket.Status is TicketStatus.Resolved or TicketStatus.Closed);
            if (!allowed) throw new SupportException("You can only close or reopen your ticket.");
        }

        ticket.Status = status;
        ticket.LastActivityAt = DateTime.UtcNow;
        ticket.ClosedAt = status is TicketStatus.Resolved or TicketStatus.Closed ? DateTime.UtcNow : null;

        var system = await _chat.AddSystemMessageAsync(ticket, user.Id,
            $"{user.FullName} changed the status to \"{UI.StatusLabel(status)}\".");

        await _realtime.MessageSentAsync(system);
        await _realtime.TicketUpdatedAsync(await GetSummaryAsync(id));

        // Notify the other side
        var link = $"/Tickets/Details/{id}";
        var title = $"{ticket.TicketNumber} is now {UI.StatusLabel(status)}";
        if (user.IsStaff && ticket.CustomerId != user.Id)
            await _notifications.NotifyAsync(ticket.CustomerId, NotificationType.StatusChanged, title, ticket.Subject, link);
        else if (user.IsCustomer && ticket.AssignedAgentId is not null)
            await _notifications.NotifyAsync(ticket.AssignedAgentId, NotificationType.StatusChanged, title, $"Changed by {user.FullName}", link);
    }

    public async Task AssignAsync(int id, string? agentId, CurrentUser user)
    {
        if (!user.IsStaff) throw new SupportException("Only support staff can assign tickets.", 403);

        // Agents can only take tickets for themselves; admins can assign anyone
        var targetId = user.IsAdmin && !string.IsNullOrWhiteSpace(agentId) ? agentId : user.Id;

        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == id) ?? throw new SupportException("Ticket not found.", 404);
        var agent = await _users.FindByIdAsync(targetId) ?? throw new SupportException("Agent not found.");
        if (!await _users.IsInRoleAsync(agent, AppRoles.Agent) && !await _users.IsInRoleAsync(agent, AppRoles.Admin))
            throw new SupportException("Tickets can only be assigned to support staff.");
        if (ticket.AssignedAgentId == agent.Id)
            throw new SupportException($"Ticket is already assigned to {agent.FullName}.");

        ticket.AssignedAgentId = agent.Id;
        ticket.LastActivityAt = DateTime.UtcNow;

        var text = agent.Id == user.Id ? $"{agent.FullName} took this ticket." : $"{user.FullName} assigned this ticket to {agent.FullName}.";
        var system = await _chat.AddSystemMessageAsync(ticket, user.Id, text);

        await _realtime.MessageSentAsync(system);
        await _realtime.TicketUpdatedAsync(await GetSummaryAsync(id));

        var link = $"/Tickets/Details/{id}";
        if (agent.Id != user.Id)
            await _notifications.NotifyAsync(agent.Id, NotificationType.TicketAssigned, $"Ticket assigned to you: {ticket.TicketNumber}", ticket.Subject, link);
        await _notifications.NotifyAsync(ticket.CustomerId, NotificationType.TicketAssigned,
            $"{agent.FullName} is now handling {ticket.TicketNumber}", ticket.Subject, link);
    }

    public async Task<DashboardViewModel> GetDashboardAsync(CurrentUser user)
    {
        var tickets = _db.Tickets.AsNoTracking();
        var vm = new DashboardViewModel { User = user };

        if (user.IsCustomer)
        {
            var mine = tickets.Where(t => t.CustomerId == user.Id);
            vm.Cards.Add(new StatCard("My Tickets", await mine.CountAsync(), "bi-ticket-perforated", "indigo"));
            vm.Cards.Add(new StatCard("Active", await mine.CountAsync(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed), "bi-hourglass-split", "amber"));
            vm.Cards.Add(new StatCard("Awaiting My Reply", await mine.CountAsync(t => t.Status == TicketStatus.WaitingForCustomer), "bi-reply", "pink"));
            vm.Cards.Add(new StatCard("Resolved", await mine.CountAsync(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed), "bi-check2-circle", "green"));
            vm.RecentTickets = await Project(mine.OrderByDescending(t => t.LastActivityAt)).Take(6).ToListAsync();
            return vm;
        }

        var assigned = tickets.Where(t => t.AssignedAgentId == user.Id);
        var unassigned = await tickets.CountAsync(t => t.AssignedAgentId == null && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);

        if (user.IsAgent)
        {
            vm.Cards.Add(new StatCard("My Active Tickets", await assigned.CountAsync(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed), "bi-headset", "indigo"));
            vm.Cards.Add(new StatCard("Unassigned Queue", unassigned, "bi-inbox", "amber"));
            vm.Cards.Add(new StatCard("High / Urgent", await assigned.CountAsync(t => (t.Priority == TicketPriority.High || t.Priority == TicketPriority.Urgent) && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed), "bi-fire", "pink"));
            vm.Cards.Add(new StatCard("Resolved by Me", await assigned.CountAsync(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed), "bi-check2-circle", "green"));
            vm.RecentTickets = await Project(assigned.OrderByDescending(t => t.LastActivityAt)).Take(6).ToListAsync();
        }
        else
        {
            vm.Agents = await GetAgentsAsync();
            vm.Cards.Add(new StatCard("Total Tickets", await tickets.CountAsync(), "bi-ticket-perforated", "indigo"));
            vm.Cards.Add(new StatCard("Active", await tickets.CountAsync(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed), "bi-hourglass-split", "amber"));
            vm.Cards.Add(new StatCard("Unassigned", unassigned, "bi-inbox", "pink"));
            vm.Cards.Add(new StatCard("Agents Online", vm.Agents.Count(a => a.IsOnline), "bi-broadcast", "green"));
            vm.RecentTickets = await Project(tickets.OrderByDescending(t => t.LastActivityAt)).Take(6).ToListAsync();
        }

        var breakdown = await tickets.GroupBy(t => t.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
        vm.StatusBreakdown = Enum.GetValues<TicketStatus>().ToDictionary(s => s, s => breakdown.FirstOrDefault(b => b.Status == s)?.Count ?? 0);
        return vm;
    }

    public async Task<List<AgentOption>> GetAgentsAsync()
    {
        var agents = await _users.GetUsersInRoleAsync(AppRoles.Agent);
        var stats = await _db.Tickets.AsNoTracking()
            .Where(t => t.AssignedAgentId != null)
            .GroupBy(t => t.AssignedAgentId!)
            .Select(g => new
            {
                AgentId = g.Key,
                Active = g.Count(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed),
                Resolved = g.Count(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed)
            })
            .ToListAsync();

        return agents.Select(a =>
            {
                var s = stats.FirstOrDefault(x => x.AgentId == a.Id);
                return new AgentOption
                {
                    Id = a.Id,
                    FullName = a.FullName,
                    Email = a.Email ?? string.Empty,
                    ActiveTickets = s?.Active ?? 0,
                    ResolvedTickets = s?.Resolved ?? 0,
                    IsOnline = _presence.IsOnline(a.Id),
                    IsActive = a.IsActive
                };
            })
            .OrderByDescending(a => a.IsOnline).ThenBy(a => a.FullName)
            .ToList();
    }

    public async Task<TicketSummaryDto> GetSummaryAsync(int id)
    {
        var t = await _db.Tickets.AsNoTracking().Include(x => x.Customer).Include(x => x.AssignedAgent).FirstAsync(x => x.Id == id);
        return new TicketSummaryDto
        {
            Id = t.Id,
            TicketNumber = t.TicketNumber,
            Subject = t.Subject,
            Status = t.Status.ToString(),
            StatusLabel = UI.StatusLabel(t.Status),
            StatusClass = UI.StatusClass(t.Status),
            Priority = t.Priority.ToString(),
            CustomerId = t.CustomerId,
            CustomerName = t.Customer.FullName,
            AssignedAgentId = t.AssignedAgentId,
            AssignedAgentName = t.AssignedAgent?.FullName,
            IsClosed = t.Status == TicketStatus.Closed
        };
    }

    // ---------- helpers ----------

    private static bool CanAccess(Ticket ticket, CurrentUser user) => user.IsStaff || ticket.CustomerId == user.Id;

    private async Task<string?> PickAgentAsync()
    {
        var agents = (await _users.GetUsersInRoleAsync(AppRoles.Agent)).Where(a => a.IsActive).ToList();
        if (agents.Count == 0) return null;

        var loads = await _db.Tickets
            .Where(t => t.AssignedAgentId != null && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .GroupBy(t => t.AssignedAgentId!)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count);

        return agents
            .OrderByDescending(a => _presence.IsOnline(a.Id))
            .ThenBy(a => loads.TryGetValue(a.Id, out var c) ? c : 0)
            .ThenBy(a => a.FullName)
            .Select(a => a.Id)
            .First();
    }

    private static IQueryable<TicketListItem> Project(IQueryable<Ticket> query) => query.Select(t => new TicketListItem
    {
        Id = t.Id,
        TicketNumber = t.TicketNumber,
        Subject = t.Subject,
        Category = t.Category,
        Priority = t.Priority,
        Status = t.Status,
        CustomerName = t.Customer.FullName,
        AgentName = t.AssignedAgent != null ? t.AssignedAgent.FullName : null,
        MessageCount = t.Messages.Count(m => !m.IsSystemMessage),
        CreatedAt = t.CreatedAt,
        LastActivityAt = t.LastActivityAt
    });
}

// =====================================================================
//  CHAT / MESSAGES
// =====================================================================

public interface IChatService
{
    Task<MessageDto> SendMessageAsync(int ticketId, CurrentUser user, string? content, IReadOnlyList<IFormFile>? files, CancellationToken ct = default);
    Task<List<MessageDto>> GetMessagesAsync(int ticketId);
    Task AttachFilesAsync(Message message, IReadOnlyList<IFormFile> files, CancellationToken ct = default);
    Task<MessageDto> AddSystemMessageAsync(Ticket ticket, string actorId, string text);
}

public class ChatService : IChatService
{
    public const int MaxMessageLength = 4000;

    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IRealtimeNotifier _realtime;
    private readonly INotificationService _notifications;

    public ChatService(AppDbContext db, IFileStorageService storage, IRealtimeNotifier realtime, INotificationService notifications)
    {
        _db = db;
        _storage = storage;
        _realtime = realtime;
        _notifications = notifications;
    }

    public async Task<MessageDto> SendMessageAsync(int ticketId, CurrentUser user, string? content, IReadOnlyList<IFormFile>? files, CancellationToken ct = default)
    {
        files ??= Array.Empty<IFormFile>();
        content = content?.Trim() ?? string.Empty;

        if (content.Length == 0 && files.Count == 0) throw new SupportException("Message cannot be empty.");
        if (content.Length > MaxMessageLength) throw new SupportException($"Message cannot exceed {MaxMessageLength} characters.");

        var ticket = await _db.Tickets.Include(t => t.AssignedAgent).FirstOrDefaultAsync(t => t.Id == ticketId, ct)
                     ?? throw new SupportException("Ticket not found.", 404);
        if (!user.IsStaff && ticket.CustomerId != user.Id) throw new SupportException("Access denied.", 403);
        if (ticket.Status == TicketStatus.Closed) throw new SupportException("This ticket is closed. Reopen it to send new messages.");

        var message = new Message { TicketId = ticket.Id, SenderId = user.Id, Content = content };
        await AttachFilesAsync(message, files, ct);
        _db.Messages.Add(message);

        // ----- Status & assignment automation -----
        var previousStatus = ticket.Status;
        var previousAgent = ticket.AssignedAgentId;
        var systemMessages = new List<Message>();

        if (user.IsStaff)
        {
            if (ticket.AssignedAgentId is null && user.IsAgent)
            {
                ticket.AssignedAgentId = user.Id;
                systemMessages.Add(NewSystem(ticket.Id, user.Id, $"{user.FullName} joined and took this ticket."));
            }
            if (ticket.Status == TicketStatus.Open) ticket.Status = TicketStatus.InProgress;
        }
        else if (ticket.Status is TicketStatus.WaitingForCustomer or TicketStatus.Resolved)
        {
            ticket.Status = TicketStatus.InProgress;
            ticket.ClosedAt = null;
        }

        ticket.LastActivityAt = DateTime.UtcNow;
        _db.Messages.AddRange(systemMessages);
        await _db.SaveChangesAsync(ct);

        // ----- Real-time broadcast -----
        var senderRole = ticket.CustomerId == user.Id ? "Customer" : "Support";
        var dto = ToDto(message, user.FullName, senderRole);
        foreach (var s in systemMessages) await _realtime.MessageSentAsync(ToDto(s, user.FullName, "System"));
        await _realtime.MessageSentAsync(dto);

        if (previousStatus != ticket.Status || previousAgent != ticket.AssignedAgentId)
            await _realtime.TicketUpdatedAsync(await BuildSummaryAsync(ticket.Id));

        // ----- Notification to the other side -----
        var preview = content.Length > 0 ? content : $"📎 {files.Count} attachment(s)";
        var title = $"New message on {ticket.TicketNumber}";
        var link = $"/Tickets/Details/{ticket.Id}";
        if (user.IsStaff && ticket.CustomerId != user.Id)
            await _notifications.NotifyAsync(ticket.CustomerId, NotificationType.NewMessage, title, $"{user.FullName}: {preview}", link);
        else if (!user.IsStaff && ticket.AssignedAgentId is not null)
            await _notifications.NotifyAsync(ticket.AssignedAgentId, NotificationType.NewMessage, title, $"{user.FullName}: {preview}", link);

        return dto;
    }

    public async Task<List<MessageDto>> GetMessagesAsync(int ticketId)
    {
        var customerId = await _db.Tickets.Where(t => t.Id == ticketId).Select(t => t.CustomerId).FirstAsync();
        var messages = await _db.Messages.AsNoTracking()
            .Where(m => m.TicketId == ticketId)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .OrderBy(m => m.Id)
            .ToListAsync();

        return messages.Select(m => ToDto(m, m.Sender.FullName,
            m.IsSystemMessage ? "System" : m.SenderId == customerId ? "Customer" : "Support")).ToList();
    }

    public async Task AttachFilesAsync(Message message, IReadOnlyList<IFormFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return;
        _storage.Validate(files);

        foreach (var file in files)
        {
            var stored = await _storage.SaveAsync(file, ct);
            message.Attachments.Add(new Attachment
            {
                OriginalFileName = stored.OriginalName,
                StoredFileName = stored.StoredName,
                ContentType = stored.ContentType,
                SizeBytes = stored.Size
            });
        }
    }

    public async Task<MessageDto> AddSystemMessageAsync(Ticket ticket, string actorId, string text)
    {
        var message = NewSystem(ticket.Id, actorId, text);
        _db.Messages.Add(message);
        await _db.SaveChangesAsync();
        return ToDto(message, "System", "System");
    }

    private async Task<TicketSummaryDto> BuildSummaryAsync(int id)
    {
        var t = await _db.Tickets.AsNoTracking().Include(x => x.Customer).Include(x => x.AssignedAgent).FirstAsync(x => x.Id == id);
        return new TicketSummaryDto
        {
            Id = t.Id, TicketNumber = t.TicketNumber, Subject = t.Subject,
            Status = t.Status.ToString(), StatusLabel = UI.StatusLabel(t.Status), StatusClass = UI.StatusClass(t.Status),
            Priority = t.Priority.ToString(), CustomerId = t.CustomerId, CustomerName = t.Customer.FullName,
            AssignedAgentId = t.AssignedAgentId, AssignedAgentName = t.AssignedAgent?.FullName,
            IsClosed = t.Status == TicketStatus.Closed
        };
    }

    private static Message NewSystem(int ticketId, string actorId, string text) =>
        new() { TicketId = ticketId, SenderId = actorId, Content = text, IsSystemMessage = true };

    public static MessageDto ToDto(Message m, string senderName, string senderRole) => new()
    {
        Id = m.Id,
        TicketId = m.TicketId,
        SenderId = m.SenderId,
        SenderName = senderName,
        SenderRole = senderRole,
        Content = m.Content,
        IsSystem = m.IsSystemMessage,
        SentAt = UI.UtcIso(m.SentAt),
        Attachments = m.Attachments.Select(a => new AttachmentDto
        {
            Id = a.Id,
            FileName = a.OriginalFileName,
            Size = UI.FileSize(a.SizeBytes),
            IsImage = a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            Url = $"/Attachments/{a.Id}"
        }).ToList()
    };
}
