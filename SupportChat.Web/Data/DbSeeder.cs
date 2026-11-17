using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SupportChat.Web.Models;

namespace SupportChat.Web.Data;

/// <summary>Creates the database, roles and demo accounts on first run.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, ILogger logger)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();

        await db.Database.EnsureCreatedAsync();

        foreach (var role in new[] { AppRoles.Admin, AppRoles.Agent, AppRoles.Customer })
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));

        if (await db.Users.AnyAsync()) return;

        async Task<ApplicationUser> CreateUser(string name, string email, string password, string role)
        {
            var user = new ApplicationUser { FullName = name, UserName = email, Email = email, EmailConfirmed = true };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, role);
            return user;
        }

        var admin = await CreateUser("Support Admin", "admin@support.com", "Admin@123", AppRoles.Admin);
        var sara = await CreateUser("Sara Ahmed", "agent1@support.com", "Agent@123", AppRoles.Agent);
        await CreateUser("Bilal Hussain", "agent2@support.com", "Agent@123", AppRoles.Agent);
        var customer = await CreateUser("Ali Raza", "customer@support.com", "Customer@123", AppRoles.Customer);

        // A sample conversation so the app is not empty on first launch
        var now = DateTime.UtcNow;
        var ticket = new Ticket
        {
            Subject = "Unable to reset my account password",
            Description = "Hi, I requested a password reset link twice but I did not receive any email. Can you please help?",
            Category = TicketCategory.Account,
            Priority = TicketPriority.High,
            Status = TicketStatus.InProgress,
            CustomerId = customer.Id,
            AssignedAgentId = sara.Id,
            CreatedAt = now.AddHours(-3),
            LastActivityAt = now.AddHours(-2)
        };
        ticket.Messages.Add(new Message { SenderId = customer.Id, Content = ticket.Description, SentAt = now.AddHours(-3) });
        ticket.Messages.Add(new Message { SenderId = admin.Id, IsSystemMessage = true, Content = "Ticket automatically assigned to Sara Ahmed.", SentAt = now.AddHours(-3) });
        ticket.Messages.Add(new Message { SenderId = sara.Id, Content = "Hello Ali! Thanks for reaching out. Could you please confirm the email address linked to your account?", SentAt = now.AddHours(-2.5) });
        ticket.Messages.Add(new Message { SenderId = customer.Id, Content = "Sure, it is customer@support.com", SentAt = now.AddHours(-2) });

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        ticket.TicketNumber = $"TKT-{ticket.Id:D5}";
        await db.SaveChangesAsync();

        logger.LogInformation("Database created and demo accounts seeded.");
    }
}
