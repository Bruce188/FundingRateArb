"use strict";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/dashboard")
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .build();

connection.on("ReceiveDashboardUpdate", (data) => {
    const botStatus = document.getElementById("bot-status");
    if (botStatus) {
        botStatus.textContent = data.botEnabled ? "RUNNING" : "STOPPED";
        botStatus.className = "badge " + (data.botEnabled ? "bg-success" : "bg-danger");
    }

    const openPositions = document.getElementById("open-positions");
    if (openPositions) openPositions.textContent = data.openPositionCount;

    const totalPnl = document.getElementById("total-pnl");
    if (totalPnl) totalPnl.textContent = "$" + (data.totalPnl ?? 0).toFixed(2);

    const bestSpread = document.getElementById("best-spread");
    if (bestSpread) bestSpread.textContent = ((data.bestSpread ?? 0) * 100).toFixed(4) + "%";
});

connection.on("ReceivePositionUpdate", (position) => {
    const positionRow = document.getElementById("position-" + position.id);
    if (positionRow) {
        const pnlEl = positionRow.querySelector(".position-pnl");
        if (pnlEl) {
            const pnl = position.unrealizedPnl ?? 0;
            pnlEl.textContent = "$" + pnl.toFixed(2);
            pnlEl.className = "position-pnl " + (pnl >= 0 ? "text-success" : "text-danger");
        }
        const spreadEl = positionRow.querySelector(".position-spread");
        if (spreadEl) {
            spreadEl.textContent = ((position.currentSpreadPerHour ?? 0) * 100).toFixed(6) + "%";
        }
    }
});

// C1 fix: replaced innerHTML with createElement + textContent for notification message
connection.on("ReceiveNotification", (message) => {
    const container = document.getElementById("notification-toast-container");
    if (!container) return;

    const toast = document.createElement("div");
    toast.className = "toast align-items-center text-bg-primary border-0 show";
    toast.setAttribute("role", "alert");

    const dFlex = document.createElement("div");
    dFlex.className = "d-flex";

    const toastBody = document.createElement("div");
    toastBody.className = "toast-body";
    toastBody.textContent = message;
    dFlex.appendChild(toastBody);

    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "btn-close btn-close-white me-2 m-auto";
    closeBtn.setAttribute("data-bs-dismiss", "toast");
    dFlex.appendChild(closeBtn);

    toast.appendChild(dFlex);
    container.appendChild(toast);

    setTimeout(() => toast.remove(), 5000);
});

// C1 fix: replaced innerHTML with createElement + textContent for opportunity data
connection.on("ReceiveOpportunityUpdate", (opportunities) => {
    const tbody = document.getElementById("opportunities-table-body");
    if (!tbody) return;

    // Update count badge
    const countBadge = document.querySelector(".badge.bg-primary");
    if (countBadge) countBadge.textContent = opportunities.length + " found";

    tbody.innerHTML = "";
    if (opportunities.length === 0) {
        const emptyRow = document.createElement("tr");
        const emptyCell = document.createElement("td");
        emptyCell.colSpan = 7;
        emptyCell.className = "text-center text-muted py-5";
        emptyCell.textContent = "No arbitrage opportunities detected at this time. Rates are fetched every minute. Check back shortly.";
        emptyRow.appendChild(emptyCell);
        tbody.appendChild(emptyRow);
        return;
    }

    const notionalPerLeg = parseFloat(tbody.dataset.notionalPerLeg) || 0;
    const volumeFraction = parseFloat(tbody.dataset.volumeFraction) || 0;

    opportunities.forEach(opp => {
        const aprClass = opp.annualizedYield > 0.50
            ? "text-success fw-bold"
            : opp.annualizedYield >= 0.20
                ? "text-warning fw-bold"
                : "";

        const constrained = volumeFraction > 0
            && Math.min(opp.longVolume24h, opp.shortVolume24h) * volumeFraction < notionalPerLeg;

        const row = document.createElement("tr");
        if (constrained) row.className = "table-warning";

        const tdAsset = document.createElement("td");
        const strong = document.createElement("strong");
        strong.textContent = opp.assetSymbol;
        tdAsset.appendChild(strong);
        row.appendChild(tdAsset);

        const tdPrice = document.createElement("td");
        const avgPrice = ((opp.longMarkPrice || 0) + (opp.shortMarkPrice || 0)) / 2;
        tdPrice.textContent = "$" + avgPrice.toFixed(2);
        row.appendChild(tdPrice);

        const tdLong = document.createElement("td");
        tdLong.textContent = opp.longExchangeName;
        row.appendChild(tdLong);

        const tdShort = document.createElement("td");
        tdShort.textContent = opp.shortExchangeName;
        row.appendChild(tdShort);

        const tdSpread = document.createElement("td");
        tdSpread.className = "text-info";
        tdSpread.textContent = (opp.spreadPerHour * 100).toFixed(4) + "%";
        row.appendChild(tdSpread);

        const tdYield = document.createElement("td");
        tdYield.textContent = (opp.netYieldPerHour * 100).toFixed(4) + "%";
        row.appendChild(tdYield);

        const tdApr = document.createElement("td");
        tdApr.className = aprClass;
        const aprText = (opp.annualizedYield * 100).toFixed(1) + "%";
        if (constrained) {
            tdApr.textContent = aprText + " ";
            const warn = document.createElement("span");
            warn.className = "ms-1";
            warn.title = "Position size limited by liquidity";
            warn.textContent = "\u26A0";
            tdApr.appendChild(warn);
        } else {
            tdApr.textContent = aprText;
        }
        row.appendChild(tdApr);

        tbody.appendChild(row);
    });
});

connection.on("ReceiveAlert", (alert) => {
    const badge = document.getElementById("alert-badge");
    if (badge) {
        const current = parseInt(badge.textContent) || 0;
        badge.textContent = current + 1;
        badge.classList.remove("d-none");
    }

    const container = document.getElementById("notification-toast-container");
    if (!container || !alert.message) return;

    const severityClass = alert.severity === 1 ? "text-bg-danger"
        : alert.severity === 2 ? "text-bg-warning"
        : "text-bg-info";

    const toast = document.createElement("div");
    toast.className = "toast align-items-center " + severityClass + " border-0 show";
    toast.setAttribute("role", "alert");

    const dFlex = document.createElement("div");
    dFlex.className = "d-flex";

    const toastBody = document.createElement("div");
    toastBody.className = "toast-body";
    toastBody.textContent = alert.message;
    dFlex.appendChild(toastBody);

    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "btn-close btn-close-white me-2 m-auto";
    closeBtn.setAttribute("data-bs-dismiss", "toast");
    dFlex.appendChild(closeBtn);

    toast.appendChild(dFlex);
    container.appendChild(toast);
    setTimeout(() => toast.remove(), 8000);
});

connection.onreconnecting(() => {
    const status = document.getElementById("connection-status");
    if (status) {
        status.className = "badge bg-warning";
        status.textContent = "Reconnecting...";
    }
});

connection.onreconnected(() => {
    const status = document.getElementById("connection-status");
    if (status) {
        status.className = "badge bg-success";
        status.textContent = "Live";
    }
});

connection.onclose(() => {
    const status = document.getElementById("connection-status");
    if (status) {
        status.className = "badge bg-danger";
        status.textContent = "Disconnected";
    }
});

const retryBtn = document.getElementById("retry-now-btn");
if (retryBtn) {
    retryBtn.addEventListener("click", async () => {
        retryBtn.disabled = true;
        retryBtn.textContent = "Triggering...";
        try {
            const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
            const resp = await fetch("/Dashboard/RetryNow", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": token
                }
            });
            const data = await resp.json();
            if (data.success) {
                retryBtn.textContent = "Triggered!";
                setTimeout(() => { retryBtn.textContent = "Retry Now"; retryBtn.disabled = false; }, 3000);
            }
        } catch (e) {
            retryBtn.textContent = "Retry Now";
            retryBtn.disabled = false;
        }
    });
}

// L1 fix: exponential backoff on initial connection failure
async function start() {
    let retryDelay = 1000; // start at 1s
    const maxDelay = 30000; // cap at 30s

    while (true) {
        try {
            await connection.start();
            const status = document.getElementById("connection-status");
            if (status) {
                status.className = "badge bg-success";
                status.textContent = "Live";
            }
            return; // connected successfully
        } catch (err) {
            console.error("SignalR connection failed, retrying in " + retryDelay + "ms:", err);
            await new Promise(resolve => setTimeout(resolve, retryDelay));
            retryDelay = Math.min(retryDelay * 2, maxDelay);
        }
    }
}

start();
