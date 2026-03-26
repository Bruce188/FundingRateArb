"use strict";

// Positions page — real-time updates via shared SignalR connection.
// Depends on signalr-connection.js which provides window.appSignalR.

(function () {
    if (!window.appSignalR) return;
    var connection = window.appSignalR.connection;

    connection.on("ReceivePositionUpdate", function (position) {
        var positionId = parseInt(position.id, 10);
        if (isNaN(positionId)) return;
        var row = document.querySelector('tr[data-position-id="' + positionId + '"]');
        if (!row) return;

        // Update current spread
        var spreadEl = row.querySelector(".pos-spread");
        if (spreadEl) {
            spreadEl.textContent = ((position.currentSpreadPerHour ?? 0) * 100).toFixed(4) + "%";
        }

        // Update accumulated funding / unrealized PnL
        var fundingEl = row.querySelector(".pos-funding");
        if (fundingEl) {
            var funding = position.accumulatedFunding ?? 0;
            fundingEl.textContent = funding.toFixed(4);
            fundingEl.className = "pos-funding text-end " + (funding >= 0 ? "text-success" : "text-danger");
        }

        // Update status badge
        var statusEl = row.querySelector(".pos-status");
        if (statusEl && position.status !== undefined) {
            var statusNames = { 0: "Opening", 1: "Open", 2: "Closing", 3: "Closed", 4: "EmergencyClosed" };
            var statusClasses = { 0: "bg-info", 1: "bg-success", 2: "bg-warning text-dark", 3: "bg-secondary", 4: "bg-danger" };
            statusEl.textContent = statusNames[position.status] || position.status;
            statusEl.className = "badge pos-status " + (statusClasses[position.status] || "bg-secondary");
        }

        // Apply warning level row class
        row.classList.remove("table-danger", "table-warning", "table-info");
        var warnLevel = position.warningLevel ?? 0;
        if (warnLevel === 3) row.classList.add("table-danger");
        else if (warnLevel === 2) row.classList.add("table-warning");
        else if (warnLevel === 1) row.classList.add("table-info");
    });

    // Update summary stats from dashboard updates
    connection.on("ReceiveDashboardUpdate", function (data) {
        var totalPnl = document.getElementById("positions-total-pnl");
        if (totalPnl) {
            totalPnl.textContent = (data.totalPnl ?? 0).toFixed(4);
        }
        var openCount = document.getElementById("positions-open-count");
        if (openCount) {
            openCount.textContent = data.openPositionCount ?? 0;
        }
    });
})();
