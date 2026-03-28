"use strict";

// Alerts page — real-time updates via shared SignalR connection.
// Depends on signalr-connection.js which provides window.appSignalR.

(function () {
    if (!window.appSignalR) return;
    var connection = window.appSignalR.connection;

    connection.on("ReceiveAlert", function (alert) {
        var tbody = document.getElementById("alerts-table-body");
        if (!tbody) return;

        // Build type badge
        var typeClasses = {
            0: "bg-success",        // OpportunityDetected
            1: "bg-warning text-dark", // SpreadWarning
            2: "bg-danger",          // SpreadCollapsed
            3: "bg-primary",         // PositionOpened
            4: "bg-secondary",       // PositionClosed
            5: "bg-danger",          // LegFailed
            6: "bg-danger",          // BotError
            7: "bg-warning text-dark" // MarginWarning
        };
        var typeNames = {
            0: "OpportunityDetected", 1: "SpreadWarning", 2: "SpreadCollapsed",
            3: "PositionOpened", 4: "PositionClosed", 5: "LegFailed",
            6: "BotError", 7: "MarginWarning"
        };

        var severityClasses = {
            0: "bg-info text-dark",    // Info
            1: "bg-danger",            // Critical
            2: "bg-warning text-dark"  // Warning
        };
        var severityNames = { 0: "Info", 1: "Critical", 2: "Warning" };

        var row = document.createElement("tr");
        row.className = "table-warning alert-new-row";

        // Type
        var tdType = document.createElement("td");
        var typeBadge = document.createElement("span");
        typeBadge.className = "badge " + (typeClasses[alert.type] || "bg-secondary");
        typeBadge.textContent = typeNames[alert.type] || alert.type;
        tdType.appendChild(typeBadge);
        row.appendChild(tdType);

        // Severity
        var tdSeverity = document.createElement("td");
        var sevBadge = document.createElement("span");
        sevBadge.className = "badge " + (severityClasses[alert.severity] || "bg-secondary");
        sevBadge.textContent = severityNames[alert.severity] || alert.severity;
        tdSeverity.appendChild(sevBadge);
        row.appendChild(tdSeverity);

        // Message
        var tdMessage = document.createElement("td");
        tdMessage.textContent = alert.message || "";
        row.appendChild(tdMessage);

        // Created — use server timestamp from payload if available, otherwise now
        var tdCreated = document.createElement("td");
        tdCreated.textContent = formatLocalDateTime(alert.createdAt || new Date().toISOString());
        row.appendChild(tdCreated);

        // Status
        var tdStatus = document.createElement("td");
        var unread = document.createElement("strong");
        unread.className = "text-warning";
        unread.textContent = "Unread";
        tdStatus.appendChild(unread);
        row.appendChild(tdStatus);

        // Actions (empty for new alerts)
        var tdActions = document.createElement("td");
        tdActions.textContent = "";
        row.appendChild(tdActions);

        // Prepend the new row
        tbody.insertBefore(row, tbody.firstChild);

        // Update the unread count badge in the page header
        var unreadBadge = document.getElementById("alerts-unread-badge");
        if (unreadBadge) {
            var current = parseInt(unreadBadge.textContent) || 0;
            unreadBadge.textContent = (current + 1) + " unread";
            unreadBadge.classList.remove("d-none");
        }

        // Brief highlight animation
        setTimeout(function () {
            row.classList.remove("alert-new-row");
        }, 3000);
    });
})();
