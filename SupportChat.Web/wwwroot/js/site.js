// =============================================================
//  SupportDesk – global client: SignalR connection, notifications,
//  toasts, presence and relative times.
// =============================================================
(function () {
    "use strict";

    const body = document.body;
    const App = window.App = {
        userId: body.dataset.userId,
        isStaff: body.dataset.staff === "true",
        isAuth: body.dataset.auth === "true",

        escape(text) {
            const div = document.createElement("div");
            div.textContent = text ?? "";
            return div.innerHTML;
        },

        token() {
            const el = document.querySelector("#antiforgeryForm input[name='__RequestVerificationToken']");
            return el ? el.value : "";
        },

        timeAgo(iso) {
            const date = new Date(iso);
            const s = Math.floor((Date.now() - date.getTime()) / 1000);
            if (s < 45) return "just now";
            if (s < 3600) return `${Math.floor(s / 60)} min ago`;
            if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
            if (s < 604800) return `${Math.floor(s / 86400)} d ago`;
            return date.toLocaleDateString();
        },

        refreshTimes(root = document) {
            root.querySelectorAll("time[data-utc]").forEach(el => {
                const d = new Date(el.dataset.utc);
                if (el.dataset.format === "full") el.textContent = d.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
                else if (el.dataset.format === "clock") el.textContent = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
                else el.textContent = App.timeAgo(el.dataset.utc);
                el.title = d.toLocaleString();
            });
        },

        toast(title, text, type = "info", link = null) {
            const area = document.getElementById("toastArea");
            const el = document.createElement(link ? "a" : "div");
            if (link) el.href = link;
            const icon = type === "success" ? "bi-check-circle-fill" : type === "danger" ? "bi-x-circle-fill" : "bi-bell-fill";
            el.className = `app-toast ${type}`;
            el.innerHTML = `<i class="bi ${icon}"></i><div><div class="t-title">${App.escape(title)}</div>${text ? `<div class="t-body">${App.escape(text)}</div>` : ""}</div>`;
            area.appendChild(el);
            setTimeout(() => { el.classList.add("hide"); setTimeout(() => el.remove(), 400); }, 5500);
        }
    };

    // Server-side TempData toasts
    document.querySelectorAll(".app-toast.auto-hide").forEach(el =>
        setTimeout(() => { el.classList.add("hide"); setTimeout(() => el.remove(), 400); }, 4000));

    // Clickable table rows
    document.addEventListener("click", e => {
        const row = e.target.closest(".clickable-row");
        if (row && !e.target.closest("a, button, form")) location.href = row.dataset.href;
    });

    App.refreshTimes();
    setInterval(() => App.refreshTimes(), 30000);

    if (!App.isAuth || typeof signalR === "undefined") return;

    // =========================================================
    //  SignalR connection (shared by all pages)
    // =========================================================
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/support")
        .withAutomaticReconnect([0, 2000, 5000, 10000, 20000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();
    App.hub = connection;

    const status = document.getElementById("connStatus");
    function setStatus(state, label) {
        if (!status) return;
        status.className = `conn-status ${state}`;
        status.querySelector(".label").textContent = label;
    }

    connection.onreconnecting(() => setStatus("", "Reconnecting…"));
    connection.onreconnected(() => {
        setStatus("live", "Live");
        document.dispatchEvent(new CustomEvent("hub:reconnected"));
    });
    connection.onclose(() => setStatus("down", "Offline"));

    // ---------- Notifications ----------
    const countEl = document.getElementById("notifCount");
    const listEl = document.getElementById("notifList");
    let unread = 0;

    const notifIcons = { TicketCreated: "bi-ticket-perforated", TicketAssigned: "bi-person-check", NewMessage: "bi-chat-dots", StatusChanged: "bi-flag" };

    function setCount(n) {
        unread = Math.max(0, n);
        countEl.textContent = unread > 99 ? "99+" : unread;
        countEl.classList.toggle("d-none", unread === 0);
    }

    function notifHtml(n) {
        return `<a class="notif-item ${n.isRead ? "" : "unread"}" href="/Notifications/Open/${n.id}">
                    <span class="n-icon"><i class="bi ${notifIcons[n.type] || "bi-bell"}"></i></span>
                    <span class="flex-grow-1 overflow-hidden">
                        <span class="n-title d-block">${App.escape(n.title)}</span>
                        <span class="n-body">${App.escape(n.body)}</span>
                        <time class="n-time" data-utc="${n.createdAt}"></time>
                    </span>
                </a>`;
    }

    async function loadNotifications() {
        try {
            const res = await fetch("/Notifications/Recent", { headers: { "Accept": "application/json" } });
            if (!res.ok) return;
            const data = await res.json();
            listEl.innerHTML = data.items.length ? data.items.map(notifHtml).join("") : `<div class="notif-empty"><i class="bi bi-bell-slash d-block fs-3 mb-2"></i>No notifications yet</div>`;
            setCount(data.unread);
            App.refreshTimes(listEl);
        } catch { /* ignore */ }
    }

    document.getElementById("markAllRead")?.addEventListener("click", async () => {
        await fetch("/Notifications/MarkAllRead", { method: "POST", headers: { "RequestVerificationToken": App.token() } });
        listEl.querySelectorAll(".notif-item.unread").forEach(el => el.classList.remove("unread"));
        setCount(0);
    });

    connection.on("Notification", n => {
        const empty = listEl.querySelector(".notif-empty");
        if (empty) empty.remove();
        listEl.insertAdjacentHTML("afterbegin", notifHtml(n));
        App.refreshTimes(listEl);
        setCount(unread + 1);
        countEl.classList.remove("bump"); void countEl.offsetWidth; countEl.classList.add("bump");

        // Don't toast a new-message notification while the user is looking at that ticket
        const openTicket = document.getElementById("chatApp")?.dataset.ticketId;
        if (n.type === "NewMessage" && openTicket && n.link === `/Tickets/Details/${openTicket}`) return;
        App.toast(n.title, n.body, "info", `/Notifications/Open/${n.id}`);
    });

    // ---------- Staff: new tickets in the queue ----------
    connection.on("TicketCreated", t => {
        if (!App.isStaff || t.assignedAgentId === App.userId) return; // assigned agent already gets a notification
        App.toast(`New ticket ${t.ticketNumber}`, `${t.customerName}: ${t.subject}`, "info", `/Tickets/Details/${t.id}`);
    });

    // ---------- Presence (online / offline dots) ----------
    connection.on("PresenceChanged", (userId, online) => {
        document.querySelectorAll(`[data-presence="${userId}"]`).forEach(el => el.classList.toggle("online", online));
    });

    // ---------- Start ----------
    App.hubReady = connection.start()
        .then(() => setStatus("live", "Live"))
        .catch(err => { console.error(err); setStatus("down", "Offline"); });

    loadNotifications();
})();
