using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportChat.Web.Models;

namespace SupportChat.Web.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.FullName).HasMaxLength(100).IsRequired();
        });

        builder.Entity<Ticket>(e =>
        {
            e.HasIndex(t => t.TicketNumber);
            e.HasIndex(t => new { t.CustomerId, t.LastActivityAt });
            e.HasIndex(t => new { t.AssignedAgentId, t.Status });
            e.Property(t => t.TicketNumber).HasMaxLength(20);
            e.Property(t => t.Subject).HasMaxLength(150).IsRequired();
            e.Property(t => t.Description).HasMaxLength(4000).IsRequired();
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Category).HasConversion<string>().HasMaxLength(30);

            e.HasOne(t => t.Customer).WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.AssignedAgent).WithMany().HasForeignKey(t => t.AssignedAgentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Message>(e =>
        {
            e.HasIndex(m => new { m.TicketId, m.Id });
            e.Property(m => m.Content).HasMaxLength(4000).IsRequired();
            e.HasOne(m => m.Ticket).WithMany(t => t.Messages).HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Attachment>(e =>
        {
            e.Property(a => a.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(a => a.StoredFileName).HasMaxLength(100).IsRequired();
            e.Property(a => a.ContentType).HasMaxLength(150).IsRequired();
            e.HasOne(a => a.Message).WithMany(m => m.Attachments).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Notification>(e =>
        {
            e.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });
            e.Property(n => n.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(n => n.Title).HasMaxLength(150).IsRequired();
            e.Property(n => n.Body).HasMaxLength(300).IsRequired();
            e.Property(n => n.Link).HasMaxLength(200);
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Adds the user's full name to the auth cookie so it is available everywhere (views, SignalR).</summary>
public class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public AppClaimsPrincipalFactory(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ClaimsPrincipalExtensions.FullNameClaim, user.FullName));
        return identity;
    }
}
