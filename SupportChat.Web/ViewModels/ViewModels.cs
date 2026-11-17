using System.ComponentModel.DataAnnotations;
using SupportChat.Web.Models;

namespace SupportChat.Web.ViewModels;

public class RegisterViewModel
{
    [Required, StringLength(100, MinimumLength = 2), Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(150)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 6), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Display(Name = "Confirm password")]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class LoginViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class CreateTicketViewModel
{
    [Required, StringLength(150, MinimumLength = 5)]
    public string Subject { get; set; } = string.Empty;

    [Required, StringLength(4000, MinimumLength = 10), Display(Name = "Describe your issue")]
    public string Description { get; set; } = string.Empty;

    public TicketCategory Category { get; set; } = TicketCategory.General;

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    public List<IFormFile>? Attachments { get; set; }
}

public class CreateAgentViewModel
{
    [Required, StringLength(100, MinimumLength = 2), Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 6), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
}

public class TicketFilter
{
    public string? Search { get; set; }
    public TicketStatus? Status { get; set; }
    public TicketPriority? Priority { get; set; }
    public TicketCategory? Category { get; set; }
    /// <summary>Staff only: mine | unassigned | all</summary>
    public string? Scope { get; set; }
    public int Page { get; set; } = 1;
}

public class TicketListItem
{
    public int Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public TicketCategory Category { get; set; }
    public TicketPriority Priority { get; set; }
    public TicketStatus Status { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string? AgentName { get; set; }
    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
}

public class TicketListViewModel
{
    public TicketFilter Filter { get; set; } = new();
    public List<TicketListItem> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public CurrentUser User { get; set; } = null!;
}

public class AgentOption
{
    public string Id { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int ActiveTickets { get; set; }
    public int ResolvedTickets { get; set; }
    public bool IsOnline { get; set; }
    public bool IsActive { get; set; }
}

public class TicketDetailsViewModel
{
    public Ticket Ticket { get; set; } = null!;
    public List<MessageDto> Messages { get; set; } = new();
    public List<AgentOption> Agents { get; set; } = new();
    public CurrentUser User { get; set; } = null!;
    public bool CustomerOnline { get; set; }
    public bool AgentOnline { get; set; }
    public bool CanReply => Ticket.Status != TicketStatus.Closed;
}

public record StatCard(string Label, int Value, string Icon, string Color);

public class DashboardViewModel
{
    public CurrentUser User { get; set; } = null!;
    public List<StatCard> Cards { get; set; } = new();
    public List<TicketListItem> RecentTickets { get; set; } = new();
    public Dictionary<TicketStatus, int> StatusBreakdown { get; set; } = new();
    public List<AgentOption> Agents { get; set; } = new();
}

public class AgentsViewModel
{
    public List<AgentOption> Agents { get; set; } = new();
    public CreateAgentViewModel NewAgent { get; set; } = new();
}
