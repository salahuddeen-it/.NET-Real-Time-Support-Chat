using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportChat.Web.Data;
using SupportChat.Web.Models;
using SupportChat.Web.Services;
using SupportChat.Web.ViewModels;

namespace SupportChat.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() =>
        User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Dashboard") : View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? code)
    {
        ViewData["Code"] = code ?? 500;
        ViewData["RequestId"] = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        return View();
    }
}

// =====================================================================
public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;

    public AccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
    {
        _users = users;
        _signIn = signIn;
    }

    [HttpGet]
    public IActionResult Register() => User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Dashboard") : View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = new ApplicationUser { FullName = model.FullName.Trim(), Email = model.Email.Trim(), UserName = model.Email.Trim() };
        var result = await _users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }

        await _users.AddToRoleAsync(user, AppRoles.Customer);
        await _signIn.SignInAsync(user, isPersistent: false);
        TempData["Success"] = $"Welcome, {user.FullName}! Your account has been created.";
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null) =>
        User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Dashboard") : View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _users.FindByEmailAsync(model.Email.Trim());
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }
        if (!user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "Your account has been deactivated.");
            return View(model);
        }

        var result = await _signIn.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return !string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl)
                ? LocalRedirect(model.ReturnUrl)
                : RedirectToAction("Index", "Dashboard");
        }

        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "Account locked for a few minutes after too many failed attempts."
            : "Invalid email or password.");
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    public IActionResult AccessDenied() => View();
}

// =====================================================================
[Authorize]
public class DashboardController : Controller
{
    private readonly ITicketService _tickets;
    public DashboardController(ITicketService tickets) => _tickets = tickets;

    public async Task<IActionResult> Index() => View(await _tickets.GetDashboardAsync(User.ToCurrentUser()));
}

// =====================================================================
[Authorize]
public class TicketsController : Controller
{
    private readonly ITicketService _tickets;
    private readonly IChatService _chat;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(ITicketService tickets, IChatService chat, ILogger<TicketsController> logger)
    {
        _tickets = tickets;
        _chat = chat;
        _logger = logger;
    }

    public async Task<IActionResult> Index([FromQuery] TicketFilter filter) =>
        View(await _tickets.GetListAsync(User.ToCurrentUser(), filter));

    [HttpGet, Authorize(Roles = AppRoles.Customer)]
    public IActionResult Create() => View(new CreateTicketViewModel());

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Customer)]
    [RequestSizeLimit(60 * 1024 * 1024)]
    public async Task<IActionResult> Create(CreateTicketViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);
        try
        {
            var ticket = await _tickets.CreateAsync(User.ToCurrentUser(), model, ct);
            TempData["Success"] = $"Ticket {ticket.TicketNumber} created. Our support team will reply shortly.";
            return RedirectToAction(nameof(Details), new { id = ticket.Id });
        }
        catch (SupportException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
    }

    public async Task<IActionResult> Details(int id)
    {
        var model = await _tickets.GetDetailsAsync(id, User.ToCurrentUser());
        return model is null ? NotFound() : View(model);
    }

    /// <summary>AJAX: send a message with optional file attachments.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(60 * 1024 * 1024)]
    public async Task<IActionResult> PostMessage(int id, [FromForm] string? content, [FromForm] List<IFormFile>? files, CancellationToken ct)
    {
        try
        {
            var message = await _chat.SendMessageAsync(id, User.ToCurrentUser(), content, files, ct);
            return Json(message);
        }
        catch (SupportException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message on ticket {Id}", id);
            return StatusCode(500, new { error = "Message could not be sent. Please try again." });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(int id, TicketStatus status)
    {
        try
        {
            await _tickets.UpdateStatusAsync(id, status, User.ToCurrentUser());
            TempData["Success"] = "Ticket status updated.";
        }
        catch (SupportException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Assign(int id, string? agentId)
    {
        try
        {
            await _tickets.AssignAsync(id, agentId, User.ToCurrentUser());
            TempData["Success"] = "Ticket assignment updated.";
        }
        catch (SupportException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }
}

// =====================================================================
[Authorize]
public class AttachmentsController : Controller
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;

    public AttachmentsController(AppDbContext db, IFileStorageService storage)
    {
        _db = db;
        _storage = storage;
    }

    /// <summary>Secure download – only people who can see the ticket can open its files.</summary>
    [HttpGet("/Attachments/{id:int}")]
    public async Task<IActionResult> Download(int id, bool download = false)
    {
        var attachment = await _db.Attachments.AsNoTracking()
            .Include(a => a.Message).ThenInclude(m => m.Ticket)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (attachment is null) return NotFound();

        var user = User.ToCurrentUser();
        if (!user.IsStaff && attachment.Message.Ticket.CustomerId != user.Id) return Forbid();

        var path = _storage.GetFullPath(attachment.StoredFileName);
        if (!System.IO.File.Exists(path)) return NotFound();

        return download
            ? PhysicalFile(path, attachment.ContentType, attachment.OriginalFileName)
            : PhysicalFile(path, attachment.ContentType);
    }
}

// =====================================================================
[Authorize]
public class NotificationsController : Controller
{
    private readonly INotificationService _notifications;
    public NotificationsController(INotificationService notifications) => _notifications = notifications;

    [HttpGet]
    public async Task<IActionResult> Recent()
    {
        var (items, unread) = await _notifications.GetRecentAsync(User.ToCurrentUser().Id);
        return Json(new { items, unread });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        await _notifications.MarkAllReadAsync(User.ToCurrentUser().Id);
        return Ok();
    }

    [HttpGet]
    public async Task<IActionResult> Open(int id)
    {
        var link = await _notifications.OpenAsync(id, User.ToCurrentUser().Id);
        return !string.IsNullOrEmpty(link) && Url.IsLocalUrl(link) ? LocalRedirect(link) : RedirectToAction("Index", "Dashboard");
    }
}

// =====================================================================
[Authorize(Roles = AppRoles.Admin)]
public class AdminController : Controller
{
    private readonly ITicketService _tickets;
    private readonly UserManager<ApplicationUser> _users;

    public AdminController(ITicketService tickets, UserManager<ApplicationUser> users)
    {
        _tickets = tickets;
        _users = users;
    }

    public async Task<IActionResult> Agents() => View(new AgentsViewModel { Agents = await _tickets.GetAgentsAsync() });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAgent([Bind(Prefix = "NewAgent")] CreateAgentViewModel model)
    {
        if (ModelState.IsValid)
        {
            var user = new ApplicationUser { FullName = model.FullName.Trim(), Email = model.Email.Trim(), UserName = model.Email.Trim(), EmailConfirmed = true };
            var result = await _users.CreateAsync(user, model.Password);
            if (result.Succeeded)
            {
                await _users.AddToRoleAsync(user, AppRoles.Agent);
                TempData["Success"] = $"Agent {user.FullName} created.";
                return RedirectToAction(nameof(Agents));
            }
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
        }

        return View("Agents", new AgentsViewModel { Agents = await _tickets.GetAgentsAsync(), NewAgent = model });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleAgent(string id)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is not null)
        {
            user.IsActive = !user.IsActive;
            await _users.UpdateAsync(user);
            TempData["Success"] = $"{user.FullName} is now {(user.IsActive ? "active" : "inactive")}.";
        }
        return RedirectToAction(nameof(Agents));
    }
}
