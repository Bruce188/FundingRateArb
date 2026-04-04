"use strict";

// Shared SignalR connection module — loaded on all authenticated pages.
// Page-specific scripts (dashboard.js, positions.js, etc.) register their
// own event handlers on window.appSignalR.connection.

(function () {
    // Centralized toast creation (must be available before dashboard.js loads)
    function showToast(message, cssClass, delay) {
        cssClass = cssClass || "text-bg-primary";
        delay = delay || 4000;

        var container = document.getElementById("notification-toast-container");
        if (!container) return;

        var toastEl = document.createElement("div");
        toastEl.className = "toast align-items-center " + cssClass + " border-0";
        toastEl.setAttribute("role", "alert");

        var dFlex = document.createElement("div");
        dFlex.className = "d-flex";

        var toastBody = document.createElement("div");
        toastBody.className = "toast-body";
        toastBody.textContent = message;
        dFlex.appendChild(toastBody);

        toastEl.appendChild(dFlex);
        container.appendChild(toastEl);

        var toast = new bootstrap.Toast(toastEl, {
            animation: true,
            autohide: true,
            delay: delay
        });
        toast.show();

        toastEl.addEventListener("hidden.bs.toast", function () { toastEl.remove(); });
    }

    // Alert toasts with close button and longer delay
    function showAlertToast(message, severityClass) {
        var container = document.getElementById("notification-toast-container");
        if (!container) return;

        var toastEl = document.createElement("div");
        toastEl.className = "toast align-items-center " + severityClass + " border-0";
        toastEl.setAttribute("role", "alert");

        var dFlex = document.createElement("div");
        dFlex.className = "d-flex";

        var toastBody = document.createElement("div");
        toastBody.className = "toast-body";
        toastBody.textContent = message;
        dFlex.appendChild(toastBody);

        var closeBtn = document.createElement("button");
        closeBtn.type = "button";
        closeBtn.className = "btn-close btn-close-white me-2 m-auto";
        closeBtn.setAttribute("data-bs-dismiss", "toast");
        dFlex.appendChild(closeBtn);

        toastEl.appendChild(dFlex);
        container.appendChild(toastEl);

        var toast = new bootstrap.Toast(toastEl, {
            animation: true,
            autohide: true,
            delay: 5000
        });
        toast.show();

        toastEl.addEventListener("hidden.bs.toast", function () { toastEl.remove(); });
    }

    var connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/dashboard")
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

    // Global handler: toast notifications on all pages
    connection.on("ReceiveNotification", function (message) {
        showToast(message, "text-bg-primary", 4000);
    });

    // Global handler: alert badge update on all pages
    connection.on("ReceiveAlert", function (alert) {
        var badge = document.getElementById("alert-badge");
        if (badge) {
            var current = parseInt(badge.textContent) || 0;
            badge.textContent = current + 1;
            badge.classList.remove("d-none");
        }

        if (!alert.message) return;

        // Skip toast when the alerts table is visible (alerts.js handles display on that page)
        if (document.getElementById("alerts-table-body")) return;

        var severityClass = alert.severity === 1 ? "text-bg-danger"
            : alert.severity === 2 ? "text-bg-warning"
            : "text-bg-info";

        showAlertToast(alert.message, severityClass);
    });

    // Connection status badge updates
    connection.onreconnecting(function () {
        var status = document.getElementById("connection-status");
        if (status) {
            status.className = "badge bg-warning";
            status.textContent = "Reconnecting...";
        }
    });

    var _lastFullUpdate = 0;

    connection.onreconnected(function () {
        var status = document.getElementById("connection-status");
        if (status) {
            status.className = "badge bg-success";
            status.textContent = "Live";
        }
        connection.invoke("RejoinGroups")
            .then(function () {
                if (Date.now() - _lastFullUpdate < 10000) return;
                return connection.invoke("RequestFullUpdate")
                    .then(function () { _lastFullUpdate = Date.now(); });
            })
            .catch(function (err) {
                console.error("Reconnection recovery failed:", err);
                if (err.toString().indexOf("Authentication required") !== -1) {
                    window.location.href = "/Identity/Account/Login";
                } else if (window.appSignalR && window.appSignalR.showToast) {
                    window.appSignalR.showToast("Reconnected but data refresh failed.", "text-bg-warning", 5000);
                }
            });
    });

    connection.onclose(function () {
        var status = document.getElementById("connection-status");
        if (status) {
            status.className = "badge bg-danger";
            status.textContent = "Disconnected";
        }
    });

    // Web Lock to prevent browser tab sleeping
    var lockResolver = null;
    if (navigator && navigator.locks && navigator.locks.request) {
        var promise = new Promise(function (res) { lockResolver = res; });
        navigator.locks.request("signalr_lock", { mode: "shared" }, function () { return promise; });
        window.addEventListener("beforeunload", function () {
            if (lockResolver) lockResolver();
        });
    }

    // Export for page-specific scripts BEFORE start() to avoid race condition
    // where page scripts check window.appSignalR before it's assigned.
    window.appSignalR = Object.freeze({
        connection: connection,
        showToast: showToast,
        showAlertToast: showAlertToast
    });

    // Exponential backoff on initial connection failure
    async function start() {
        var retryDelay = 1000;
        var maxDelay = 30000;

        while (true) {
            try {
                await connection.start();
                var status = document.getElementById("connection-status");
                if (status) {
                    status.className = "badge bg-success";
                    status.textContent = "Live";
                }
                return;
            } catch (err) {
                console.error("SignalR connection failed, retrying in " + retryDelay + "ms:", err);
                await new Promise(function (resolve) { setTimeout(resolve, retryDelay); });
                retryDelay = Math.min(retryDelay * 2, maxDelay);
            }
        }
    }

    start().catch(function (err) {
        console.error("SignalR start failed:", err);
    });
})();
