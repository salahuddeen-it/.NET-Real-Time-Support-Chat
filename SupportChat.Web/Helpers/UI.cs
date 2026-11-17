using SupportChat.Web.Models;

namespace SupportChat.Web.Helpers;

/// <summary>Display helpers shared by views and DTO mapping.</summary>
public static class UI
{
    public static string StatusLabel(TicketStatus status) => status switch
    {
        TicketStatus.InProgress => "In Progress",
        TicketStatus.WaitingForCustomer => "Waiting for Customer",
        _ => status.ToString()
    };

    public static string StatusClass(TicketStatus status) => status switch
    {
        TicketStatus.Open => "st-open",
        TicketStatus.InProgress => "st-progress",
        TicketStatus.WaitingForCustomer => "st-waiting",
        TicketStatus.Resolved => "st-resolved",
        _ => "st-closed"
    };

    public static string PriorityClass(TicketPriority priority) => priority switch
    {
        TicketPriority.Urgent => "pr-urgent",
        TicketPriority.High => "pr-high",
        TicketPriority.Medium => "pr-medium",
        _ => "pr-low"
    };

    public static string CategoryIcon(TicketCategory category) => category switch
    {
        TicketCategory.Technical => "bi-cpu",
        TicketCategory.Billing => "bi-credit-card",
        TicketCategory.Account => "bi-person-lock",
        TicketCategory.FeatureRequest => "bi-lightbulb",
        TicketCategory.Other => "bi-three-dots",
        _ => "bi-chat-square-text"
    };

    public static string CategoryLabel(TicketCategory category) =>
        category == TicketCategory.FeatureRequest ? "Feature Request" : category.ToString();

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? parts[0][..1].ToUpper() : $"{parts[0][0]}{parts[^1][0]}".ToUpper();
    }

    private static readonly string[] AvatarColors = { "#4f46e5", "#0891b2", "#16a34a", "#d97706", "#db2777", "#7c3aed", "#0d9488", "#dc2626" };

    public static string AvatarColor(string? id)
    {
        if (string.IsNullOrEmpty(id)) return AvatarColors[0];
        var hash = id.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        return AvatarColors[Math.Abs(hash % AvatarColors.Length)];
    }

    public static string FileSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    /// <summary>ISO-8601 UTC string (with 'Z') so the browser shows the correct local time.</summary>
    public static string UtcIso(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o");
}
