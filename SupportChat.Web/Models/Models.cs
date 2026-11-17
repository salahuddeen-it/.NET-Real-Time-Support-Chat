using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace SupportChat.Web.Models;

// ======================= ENUMS & ROLES =======================

public enum TicketStatus { Open = 1, InProgress = 2, WaitingForCustomer = 3, Resolved = 4, Closed = 5 }

public enum TicketPriority { Low = 1, Medium = 2, High = 3, Urgent = 4 }

public enum TicketCategory { General = 1, Technical = 2, Billing = 3, Account = 4, FeatureRequest = 5, Other = 6 }

public enum NotificationType { TicketCreated = 1, TicketAssigned = 2, NewMessage = 3, StatusChanged = 4 }

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Agent = "Agent";
    public const string Customer = "Customer";
    public const string Staff = Agent + "," + Admin;
}

// ======================= ENTITIES =======================

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Ticket
{
    public int Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketCategory Category { get; set; } = TicketCategory.General;
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public string CustomerId { get; set; } = string.Empty;
    public ApplicationUser Customer { get; set; } = null!;

    public string? AssignedAgentId { get; set; }
    public ApplicationUser? AssignedAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();

    public bool IsActive => Status is not (TicketStatus.Resolved or TicketStatus.Closed);
}

public class Message
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public string SenderId { get; set; } = string.Empty;
    public ApplicationUser Sender { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public bool IsSystemMessage { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}

public class Attachment
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ======================= DTOs (sent to browser / SignalR) =======================

public class MessageDto
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderRole { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public string SentAt { get; set; } = string.Empty;
    public List<AttachmentDto> Attachments { get; set; } = new();
}

public class AttachmentDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public bool IsImage { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class TicketSummaryDto
{
    public int Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string StatusClass { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? AssignedAgentId { get; set; }
    public string? AssignedAgentName { get; set; }
    public bool IsClosed { get; set; }
}

public class NotificationDto
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

// ======================= CURRENT USER & ERRORS =======================

public record CurrentUser(string Id, string FullName, string Role)
{
    public bool IsAdmin => Role == AppRoles.Admin;
    public bool IsAgent => Role == AppRoles.Agent;
    public bool IsStaff => IsAdmin || IsAgent;
    public bool IsCustomer => Role == AppRoles.Customer;
}

public static class ClaimsPrincipalExtensions
{
    public const string FullNameClaim = "full_name";

    public static CurrentUser ToCurrentUser(this ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new SupportException("You are not logged in.", 401);
        var name = principal.FindFirstValue(FullNameClaim) ?? principal.Identity?.Name ?? "User";
        var role = principal.IsInRole(AppRoles.Admin) ? AppRoles.Admin
                 : principal.IsInRole(AppRoles.Agent) ? AppRoles.Agent
                 : AppRoles.Customer;
        return new CurrentUser(id, name, role);
    }
}

/// <summary>Business errors that are safe to show to the user.</summary>
public class SupportException : Exception
{
    public int StatusCode { get; }
    public SupportException(string message, int statusCode = 400) : base(message) => StatusCode = statusCode;
}
