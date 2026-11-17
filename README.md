# 💬 SupportDesk – Real-Time Customer Support Chat

A real-time customer-support system built with **ASP.NET Core 8 MVC**, **SignalR**, **ASP.NET Core Identity**, **Entity Framework Core** and **SQL Server**. Customers create support tickets and chat live with support agents.

---

## ✅ Features

| Requirement | Implementation |
|---|---|
| User registration / login | ASP.NET Core Identity (hashed passwords, lockout after 5 failed attempts, remember me) |
| Customer & support-agent roles | Roles: **Customer**, **Agent**, **Admin** with role-based authorization |
| Create support tickets | Subject, description, category, priority + optional attachments; ticket number `TKT-00001` |
| Real-time messaging | **SignalR** hub – messages appear instantly for everyone in the ticket room |
| Ticket status | Open → In Progress → Waiting for Customer → Resolved → Closed (auto-updates on replies, customers can close / reopen) |
| Message history | Full conversation stored in SQL Server and loaded on the ticket page, with system events in the timeline |
| Agent assignment | **Automatic** on ticket creation (online agents first, lowest workload), agents can *take* tickets, admin can reassign |
| File attachment support | Images, PDF, Word, Excel, TXT, ZIP (max 5 files × 10 MB), drag & drop and paste screenshots, inline image preview, secure download |
| Notifications | Stored in DB + pushed live via SignalR: notification bell with unread count, toast pop-ups, mark-all-read |

### ⭐ Extra real-time features
- **Typing indicator** ("Sara is typing…")
- **Online / offline presence** for agents and customers
- **Live connection status** badge with automatic reconnect
- Live toast for agents when a new ticket enters the queue
- Dashboards for Customer, Agent and Admin, ticket search, filters and pagination
- Admin: manage agents (create, activate / deactivate) with workload overview
- Secure: attachments stored outside `wwwroot` and served only to authorized users, anti-forgery protection, input validation

---

## 🛠 Tech Stack
ASP.NET Core 8 MVC · SignalR · ASP.NET Core Identity · Entity Framework Core 8 · SQL Server (LocalDB) · Bootstrap 5 · Vanilla JS

---

## 🚀 How to Run
1. Open `SupportChat.sln` in **Visual Studio 2022** (.NET 8 SDK).
2. Press **F5**. The database `SupportChatDb`, roles and demo accounts are created automatically.

### 🔑 Demo accounts
| Role | Email | Password |
|---|---|---|
| Customer | customer@support.com | Customer@123 |
| Agent | agent1@support.com | Agent@123 |
| Agent | agent2@support.com | Agent@123 |
| Admin | admin@support.com | Admin@123 |

> On the login page you can click a demo account to fill it in.

### 🧪 See it working in real time
1. Open the app in **two different browsers** (e.g. Chrome and Edge, or one normal + one Incognito window).
2. Login as **customer** in one and **agent1** in the other.
3. Customer: **New Ticket** → submit. The agent instantly gets a notification 🔔.
4. Open the same ticket in both windows and chat – messages, typing indicator, status changes and attachments update live.

---

## 🧩 Architecture
```
Browser (Bootstrap + JS) ──HTTP──► MVC Controllers ──► Services ──► EF Core ──► SQL Server
        ▲                                                │
        └────────── SignalR (/hubs/support) ◄────────────┘  (IRealtimeNotifier)
```

```
SupportChat.Web/
├── Controllers/   Home, Account, Dashboard, Tickets, Attachments, Notifications, Admin
├── Hubs/          SupportHub (rooms, typing, presence), PresenceTracker, SignalR notifier
├── Services/      TicketService, ChatService, NotificationService, FileStorageService
├── Data/          AppDbContext (Identity + tables), DbSeeder, claims factory
├── Models/        Entities, enums, DTOs
├── ViewModels/    Forms and page models with validation
├── Views/         Razor pages (dashboard, ticket list, live chat, admin)
└── wwwroot/       site.css, site.js (SignalR + notifications), chat.js (live chat)
```

### SignalR events
| Direction | Method | Purpose |
|---|---|---|
| Client → Server | `JoinTicket`, `LeaveTicket` | Join / leave a ticket chat room (access checked) |
| Client → Server | `SendMessage` | Send a text message |
| Client → Server | `Typing` | Typing indicator |
| Server → Client | `ReceiveMessage` | New message in the ticket |
| Server → Client | `TicketUpdated` | Status / assignment changed |
| Server → Client | `TicketCreated` | New ticket for staff |
| Server → Client | `Notification` | Personal notification |
| Server → Client | `UserTyping`, `PresenceChanged` | Typing and online status |

---
**Developer:** Syed Muhammad Shakeel · [GitHub](https://github.com/salahuddeen-it)
