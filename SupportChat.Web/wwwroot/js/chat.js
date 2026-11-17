// =============================================================
//  SupportDesk – real-time ticket chat
// =============================================================
(function () {
    "use strict";

    const root = document.getElementById("chatApp");
    if (!root || !window.App || !App.hub) return;

    const hub = App.hub;
    const ticketId = parseInt(root.dataset.ticketId, 10);
    const me = root.dataset.userId;

    const list = document.getElementById("messageList");
    const input = document.getElementById("messageInput");
    const sendBtn = document.getElementById("sendBtn");
    const attachBtn = document.getElementById("attachBtn");
    const fileInput = document.getElementById("fileInput");
    const previews = document.getElementById("filePreviews");
    const errorEl = document.getElementById("composerError");
    const typingLine = document.getElementById("typingLine");
    const typingName = document.getElementById("typingName");
    const composer = document.getElementById("composer");
    const closedBanner = document.getElementById("closedBanner");

    const MAX_FILES = 5;
    const MAX_SIZE = 10 * 1024 * 1024;
    const rendered = new Set();
    let selectedFiles = [];
    let sending = false;

    // ---------------- Rendering ----------------
    const fileIcon = name => {
        const ext = name.split(".").pop().toLowerCase();
        if (ext === "pdf") return "bi-file-earmark-pdf";
        if (["doc", "docx"].includes(ext)) return "bi-file-earmark-word";
        if (["xls", "xlsx", "csv"].includes(ext)) return "bi-file-earmark-excel";
        if (ext === "zip") return "bi-file-earmark-zip";
        return "bi-file-earmark-text";
    };

    function initials(name) {
        const parts = (name || "?").trim().split(/\s+/);
        return (parts.length === 1 ? parts[0][0] : parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
    }

    function colorFor(id) {
        const colors = ["#4f46e5", "#0891b2", "#16a34a", "#d97706", "#db2777", "#7c3aed", "#0d9488", "#dc2626"];
        let h = 17;
        for (const c of id || "") h = (h * 31 + c.charCodeAt(0)) | 0;
        return colors[Math.abs(h % colors.length)];
    }

    function messageHtml(m) {
        if (m.isSystem) {
            return `<div class="msg-system" data-id="${m.id}"><span><i class="bi bi-info-circle me-1"></i>${App.escape(m.content)} · <time data-utc="${m.sentAt}" data-format="clock"></time></span></div>`;
        }

        const mine = m.senderId === me;
        const atts = (m.attachments || []).map(a => a.isImage
            ? `<a href="${a.url}" target="_blank" title="${App.escape(a.fileName)}"><img src="${a.url}" class="att-img" alt="${App.escape(a.fileName)}" loading="lazy" /></a>`
            : `<a href="${a.url}?download=true" class="att-file"><i class="bi ${fileIcon(a.fileName)}"></i><span class="overflow-hidden"><span class="att-name d-block">${App.escape(a.fileName)}</span><span class="att-size">${a.size}</span></span><i class="bi bi-download small ms-1"></i></a>`
        ).join("");

        return `
            <div class="msg ${mine ? "mine" : ""}" data-id="${m.id}">
                <span class="avatar sm" style="background:${colorFor(m.senderId)}">${App.escape(initials(m.senderName))}</span>
                <div class="bubble-wrap">
                    <div class="meta">
                        <span class="fw-semibold text-dark">${mine ? "You" : App.escape(m.senderName)}</span>
                        ${m.senderRole === "Support" ? `<span class="role-tag">Support</span>` : ""}
                        <time data-utc="${m.sentAt}" data-format="clock"></time>
                    </div>
                    <div class="bubble">${App.escape(m.content)}</div>
                    ${atts ? `<div class="attachments">${atts}</div>` : ""}
                </div>
            </div>`;
    }

    function render(m, scroll = true) {
        if (!m || rendered.has(m.id)) return;
        rendered.add(m.id);
        list.insertAdjacentHTML("beforeend", messageHtml(m));
        App.refreshTimes(list.lastElementChild);
        if (scroll) scrollToBottom();
    }

    function scrollToBottom() {
        requestAnimationFrame(() => { list.scrollTop = list.scrollHeight; });
    }

    // Initial history
    try {
        const initial = JSON.parse(document.getElementById("initialMessages").textContent || "[]");
        initial.forEach(m => render(m, false));
    } catch (e) { console.error(e); }
    list.style.scrollBehavior = "auto";
    list.scrollTop = list.scrollHeight;
    list.style.scrollBehavior = "";
    list.querySelectorAll("img").forEach(img => img.addEventListener("load", () => { list.scrollTop = list.scrollHeight; }, { once: true }));

    // ---------------- SignalR events ----------------
    hub.on("ReceiveMessage", m => {
        if (m.ticketId !== ticketId) return;
        render(m);
        if (m.senderId !== me) showTyping(false);
    });

    let typingTimer;
    function showTyping(on, name) {
        clearTimeout(typingTimer);
        if (on) {
            typingName.textContent = name;
            typingLine.classList.add("show");
            typingTimer = setTimeout(() => typingLine.classList.remove("show"), 6000);
        } else {
            typingLine.classList.remove("show");
        }
    }

    hub.on("UserTyping", (id, name, isTyping) => {
        if (id === ticketId) showTyping(isTyping, name);
    });

    hub.on("TicketUpdated", t => {
        if (t.id !== ticketId) return;
        ["statusBadge", "statusBadgeSide"].forEach(id => {
            const el = document.getElementById(id);
            if (el) { el.className = `status-pill ${t.statusClass}${id === "statusBadge" ? " ms-auto" : ""}`; el.textContent = t.statusLabel; }
        });
        const agent = document.getElementById("assignedAgent");
        if (agent) agent.textContent = t.assignedAgentName || "Not assigned yet";
        const waiting = document.getElementById("counterpartName");
        if (waiting && t.assignedAgentName) waiting.textContent = t.assignedAgentName;

        composer.classList.toggle("d-none", t.isClosed);
        closedBanner.classList.toggle("d-none", !t.isClosed);
    });

    function joinRoom() {
        return hub.invoke("JoinTicket", ticketId).catch(err => showError(cleanError(err)));
    }
    App.hubReady.then(joinRoom);
    document.addEventListener("hub:reconnected", joinRoom);
    window.addEventListener("beforeunload", () => { try { hub.invoke("LeaveTicket", ticketId); } catch { } });

    // ---------------- Sending ----------------
    function cleanError(err) {
        const msg = (err && err.message) || String(err);
        const idx = msg.indexOf("HubException:");
        return idx >= 0 ? msg.substring(idx + 13).trim() : "Message could not be sent. Please try again.";
    }

    function showError(text) {
        errorEl.textContent = text;
        errorEl.classList.toggle("d-none", !text);
    }

    async function send() {
        if (sending) return;
        const text = input.value.trim();
        if (!text && selectedFiles.length === 0) return;

        sending = true;
        sendBtn.disabled = true;
        sendBtn.innerHTML = `<span class="spinner-border spinner-border-sm"></span>`;
        showError("");
        notifyTyping(false);

        try {
            if (selectedFiles.length) {
                const form = new FormData();
                form.append("content", text);
                selectedFiles.forEach(f => form.append("files", f));
                const res = await fetch(`/Tickets/PostMessage/${ticketId}`, {
                    method: "POST",
                    headers: { "RequestVerificationToken": App.token() },
                    body: form
                });
                const data = await res.json().catch(() => ({}));
                if (!res.ok) throw new Error(data.error || "Upload failed. Please try again.");
                render(data);
            } else {
                const message = await hub.invoke("SendMessage", ticketId, text);
                render(message);
            }
            input.value = "";
            autoResize();
            clearFiles();
        } catch (err) {
            showError(err.message && !err.message.includes("HubException") && !err.message.includes("invoking") ? err.message : cleanError(err));
        } finally {
            sending = false;
            sendBtn.disabled = false;
            sendBtn.innerHTML = `<i class="bi bi-send-fill"></i>`;
            input.focus();
        }
    }

    sendBtn.addEventListener("click", send);
    input.addEventListener("keydown", e => {
        if (e.key === "Enter" && !e.shiftKey) {
            e.preventDefault();
            send();
        }
    });

    // Auto-grow textarea
    function autoResize() {
        input.style.height = "auto";
        input.style.height = Math.min(input.scrollHeight, 160) + "px";
    }
    input.addEventListener("input", autoResize);

    // ---------------- Typing indicator ----------------
    let lastTypingSent = 0;
    let stopTypingTimer;
    function notifyTyping(isTyping) {
        if (hub.state !== signalR.HubConnectionState.Connected) return;
        hub.invoke("Typing", ticketId, isTyping).catch(() => { });
    }
    input.addEventListener("input", () => {
        const now = Date.now();
        if (now - lastTypingSent > 2500) {
            lastTypingSent = now;
            notifyTyping(true);
        }
        clearTimeout(stopTypingTimer);
        stopTypingTimer = setTimeout(() => { lastTypingSent = 0; notifyTyping(false); }, 3000);
    });

    // ---------------- Attachments ----------------
    function addFiles(files) {
        showError("");
        for (const f of files) {
            if (selectedFiles.length >= MAX_FILES) { showError(`You can attach up to ${MAX_FILES} files.`); break; }
            if (f.size > MAX_SIZE) { showError(`'${f.name}' is larger than 10 MB.`); continue; }
            selectedFiles.push(f);
        }
        renderPreviews();
    }

    function renderPreviews() {
        previews.innerHTML = selectedFiles.map((f, i) =>
            `<span class="file-chip"><i class="bi ${f.type.startsWith("image/") ? "bi-image" : fileIcon(f.name)}"></i>${App.escape(f.name)}<button type="button" data-index="${i}" title="Remove"><i class="bi bi-x-lg"></i></button></span>`
        ).join("");
    }

    function clearFiles() {
        selectedFiles = [];
        fileInput.value = "";
        renderPreviews();
    }

    previews.addEventListener("click", e => {
        const btn = e.target.closest("button[data-index]");
        if (!btn) return;
        selectedFiles.splice(parseInt(btn.dataset.index, 10), 1);
        renderPreviews();
    });

    attachBtn.addEventListener("click", () => fileInput.click());
    fileInput.addEventListener("change", () => { addFiles(fileInput.files); fileInput.value = ""; });

    // Drag & drop onto the chat
    ["dragenter", "dragover"].forEach(evt => root.addEventListener(evt, e => {
        if (composer.classList.contains("d-none")) return;
        e.preventDefault();
        root.classList.add("drag-over");
    }));
    ["dragleave", "drop"].forEach(evt => root.addEventListener(evt, e => {
        e.preventDefault();
        if (evt === "drop" || !root.contains(e.relatedTarget)) root.classList.remove("drag-over");
    }));
    root.addEventListener("drop", e => {
        if (!composer.classList.contains("d-none") && e.dataTransfer.files.length) addFiles(e.dataTransfer.files);
    });

    // Paste screenshots directly
    input.addEventListener("paste", e => {
        const files = [...(e.clipboardData?.files || [])];
        if (files.length) { e.preventDefault(); addFiles(files); }
    });
})();
