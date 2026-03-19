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
    if (totalPnl) totalPnl.textContent = "$" + data.totalPnl.toFixed(2);

    const bestSpread = document.getElementById("best-spread");
    if (bestSpread) bestSpread.textContent = (data.bestSpread * 100).toFixed(4) + "%";
});

// C1 fix: replaced innerHTML with createElement + textContent for all server data
connection.on("ReceiveFundingRateUpdate", (rates) => {
    const tbody = document.getElementById("rates-table-body");
    if (!tbody) return;

    tbody.innerHTML = "";
    rates.forEach(r => {
        const row = document.createElement("tr");

        const tdExchange = document.createElement("td");
        tdExchange.textContent = r.exchangeName;
        row.appendChild(tdExchange);

        const tdSymbol = document.createElement("td");
        tdSymbol.textContent = r.symbol;
        row.appendChild(tdSymbol);

        const tdRate = document.createElement("td");
        tdRate.className = r.ratePerHour >= 0 ? "text-success" : "text-danger";
        tdRate.textContent = (r.ratePerHour * 100).toFixed(6) + "%";
        row.appendChild(tdRate);

        const tdPrice = document.createElement("td");
        tdPrice.textContent = "$" + r.markPrice.toLocaleString();
        row.appendChild(tdPrice);

        tbody.appendChild(row);
    });
});

connection.on("ReceivePositionUpdate", (position) => {
    const positionRow = document.getElementById("position-" + position.id);
    if (positionRow) {
        positionRow.querySelector(".position-pnl").textContent = "$" + position.unrealizedPnl.toFixed(2);
        positionRow.querySelector(".position-spread").textContent = (position.currentSpreadPerHour * 100).toFixed(6) + "%";
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
        emptyCell.colSpan = 6;
        emptyCell.className = "text-center text-muted py-5";
        emptyCell.textContent = "No arbitrage opportunities detected at this time. Rates are fetched every minute. Check back shortly.";
        emptyRow.appendChild(emptyCell);
        tbody.appendChild(emptyRow);
        return;
    }

    opportunities.forEach(opp => {
        const aprClass = opp.annualizedYield > 0.50
            ? "text-success fw-bold"
            : opp.annualizedYield >= 0.20
                ? "text-warning fw-bold"
                : "";
        const row = document.createElement("tr");

        const tdAsset = document.createElement("td");
        const strong = document.createElement("strong");
        strong.textContent = opp.assetSymbol;
        tdAsset.appendChild(strong);
        row.appendChild(tdAsset);

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
        tdApr.textContent = (opp.annualizedYield * 100).toFixed(1) + "%";
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
