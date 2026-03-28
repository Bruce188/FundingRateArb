"use strict";

// Dashboard-specific SignalR event handlers.
// Depends on signalr-connection.js which provides window.appSignalR.

(function () {
    if (!window.appSignalR) return;
    var connection = window.appSignalR.connection;
    var showToast = window.appSignalR.showToast;

    // Dynamic decimal formatting for small prices
    function formatPrice(price) {
        if (price === 0) return "$0.00";
        if (price >= 1) return "$" + price.toFixed(2);
        if (price >= 0.01) return "$" + price.toFixed(4);
        return "$" + parseFloat(price.toFixed(8)).toPrecision(4);
    }

    connection.on("ReceiveDashboardUpdate", function (data) {
        var botStatus = document.getElementById("bot-status");
        if (botStatus) {
            botStatus.textContent = data.botEnabled ? "RUNNING" : "STOPPED";
            botStatus.className = "badge " + (data.botEnabled ? "bg-success" : "bg-danger");
        }

        var openPositions = document.getElementById("open-positions");
        if (openPositions) openPositions.textContent = data.openPositionCount;

        var totalPnl = document.getElementById("total-pnl");
        if (totalPnl) totalPnl.textContent = (data.totalPnl ?? 0).toFixed(4);

        var bestSpread = document.getElementById("best-spread");
        if (bestSpread) {
            var spread = data.bestSpread ?? 0;
            bestSpread.textContent = spread > 0
                ? (spread * 100).toFixed(4) + "%"
                : "N/A";
        }
    });

    connection.on("ReceivePositionUpdate", function (position) {
        var positionRow = document.getElementById("position-" + position.id);
        if (positionRow) {
            var pnlEl = positionRow.querySelector(".position-pnl");
            if (pnlEl) {
                var pnl = position.unrealizedPnl ?? 0;
                pnlEl.textContent = "$" + pnl.toFixed(2);
                pnlEl.className = "position-pnl " + (pnl >= 0 ? "text-success" : "text-danger");
            }
            var spreadEl = positionRow.querySelector(".position-spread");
            if (spreadEl) {
                spreadEl.textContent = ((position.currentSpreadPerHour ?? 0) * 100).toFixed(6) + "%";
            }

            // Apply warning level row class
            positionRow.classList.remove("table-danger", "table-warning", "table-info");
            var warnLevel = position.warningLevel ?? 0;
            if (warnLevel === 3) positionRow.classList.add("table-danger");
            else if (warnLevel === 2) positionRow.classList.add("table-warning");
            else if (warnLevel === 1) positionRow.classList.add("table-info");

            // Update warning type icons in the first cell
            var firstTd = positionRow.querySelector("td:first-child");
            if (firstTd) {
                var existing = firstTd.querySelectorAll(".warning-icon");
                existing.forEach(function (el) { el.remove(); });

                var warnTypes = position.warningTypes || [];
                var iconMap = { 0: "\u25B2", 1: "\u2248", 2: "\u23F0", 3: "\u26BF", 4: "\u2193" };
                var nameMap = { 0: "SpreadRisk", 1: "Liquidity", 2: "TimeBased", 3: "Leverage", 4: "Loss" };
                warnTypes.forEach(function (wt) {
                    var span = document.createElement("span");
                    span.className = "warning-icon warning-" + (nameMap[wt] || "").toLowerCase();
                    span.title = nameMap[wt] || "";
                    span.textContent = iconMap[wt] || "";
                    firstTd.appendChild(span);
                });
            }
        }
    });

    var isMobile = window.matchMedia('(max-width: 575.98px)');

    // C1 fix: replaced innerHTML with createElement + textContent for opportunity data
    connection.on("ReceiveOpportunityUpdate", function (data) {
        var tbody = document.getElementById("opportunities-table-body");
        var cardsContainer = document.getElementById("opportunities-cards");

        // Rebuild mobile cards
        if (cardsContainer && isMobile.matches) {
            var opportunities = data.opportunities || [];
            cardsContainer.replaceChildren();
            if (opportunities.length === 0) {
                var p = document.createElement("p");
                p.className = "text-muted text-center py-3 mb-0";
                p.textContent = "No opportunities detected.";
                cardsContainer.appendChild(p);
            } else {
                opportunities.forEach(function (opp) {
                    var card = document.createElement("div");
                    card.className = "card mb-2 mobile-card";
                    var body = document.createElement("div");
                    body.className = "card-body p-2";

                    var header = document.createElement("div");
                    header.className = "d-flex justify-content-between";
                    var strong = document.createElement("strong");
                    strong.textContent = opp.assetSymbol;
                    header.appendChild(strong);
                    var badge = document.createElement("span");
                    var apyVal = (opp.annualizedYield * 100).toFixed(0);
                    badge.className = "badge " + (opp.annualizedYield > 0.50 ? "bg-success" : opp.annualizedYield >= 0.20 ? "bg-warning text-dark" : "bg-secondary");
                    badge.textContent = apyVal + "% APY";
                    header.appendChild(badge);
                    body.appendChild(header);

                    var exchanges = document.createElement("small");
                    exchanges.className = "text-muted";
                    exchanges.textContent = "Long: " + opp.longExchangeName + " | Short: " + opp.shortExchangeName;
                    body.appendChild(exchanges);

                    var spreadDiv = document.createElement("div");
                    spreadDiv.className = "small";
                    spreadDiv.textContent = "Spread: " + (opp.spreadPerHour * 100).toFixed(4) + "%/hr | Net: " + ((opp.boostedNetYieldPerHour ?? opp.netYieldPerHour) * 100).toFixed(4) + "%/hr";
                    body.appendChild(spreadDiv);

                    card.appendChild(body);
                    cardsContainer.appendChild(card);
                });
            }
        }

        if (!tbody) return;

        var opportunitiesArr = data.opportunities || [];
        var diagnostics = data.diagnostics;

        // Update count badge
        var countBadge = document.querySelector(".badge.bg-primary");
        if (countBadge) countBadge.textContent = opportunitiesArr.length + " found";

        // Update Best Spread KPI from diagnostics (raw spread includes all opportunities, not just above-threshold)
        if (diagnostics && diagnostics.bestRawSpread > 0) {
            var bestSpread = document.getElementById("best-spread");
            if (bestSpread) {
                bestSpread.textContent = (diagnostics.bestRawSpread * 100).toFixed(4) + "%";
            }
        }

        tbody.replaceChildren();
        if (opportunitiesArr.length === 0) {
            var emptyRow = document.createElement("tr");
            var emptyCell = document.createElement("td");
            emptyCell.colSpan = 8;
            emptyCell.className = "text-center py-4";

            if (diagnostics) {
                var alertDiv = document.createElement("div");
                alertDiv.className = "mb-0 d-inline-block";

                if (diagnostics.totalRatesLoaded === 0) {
                    alertDiv.className += " alert alert-warning";
                    alertDiv.textContent = "No funding rate data available. Background services may not be running.";
                } else if (diagnostics.ratesAfterStalenessFilter === 0) {
                    alertDiv.className += " alert alert-warning";
                    alertDiv.textContent = "All " + diagnostics.totalRatesLoaded + " rates are older than " + diagnostics.stalenessMinutes + " minutes. Exchange connections may be down.";
                } else if (diagnostics.pairsPassing === 0 && diagnostics.pairsFilteredByVolume > 0 && diagnostics.pairsFilteredByThreshold === 0) {
                    alertDiv.className += " alert alert-info";
                    alertDiv.textContent = diagnostics.pairsFilteredByVolume + " pairs filtered \u2014 volume below $" + diagnostics.minVolumeThreshold.toLocaleString("en-US", { maximumFractionDigits: 0 }) + " on one or both legs.";
                } else if (diagnostics.pairsPassing === 0 && (diagnostics.pairsFilteredByThreshold > 0 || diagnostics.netPositiveBelowThreshold > 0)) {
                    alertDiv.className += " alert alert-info";
                    var totalBelowThreshold = diagnostics.pairsFilteredByThreshold + (diagnostics.netPositiveBelowThreshold || 0);
                    var thresholdText = totalBelowThreshold + " pairs below " + (diagnostics.openThreshold * 100).toFixed(3) + "% net yield threshold. ";
                    if (diagnostics.netPositiveBelowThreshold > 0) {
                        var bold = document.createElement("strong");
                        bold.textContent = diagnostics.netPositiveBelowThreshold + " profitable (adaptive eligible).";
                        alertDiv.textContent = thresholdText;
                        alertDiv.appendChild(bold);
                        var trailing = document.createTextNode(" Best raw spread: " + (diagnostics.bestRawSpread * 100).toFixed(4) + "%.");
                        alertDiv.appendChild(trailing);
                    } else {
                        alertDiv.textContent = thresholdText + "Best raw spread: " + (diagnostics.bestRawSpread * 100).toFixed(4) + "%.";
                    }
                } else {
                    var span = document.createElement("span");
                    span.className = "text-muted";
                    span.textContent = "No arbitrage opportunities detected at this time.";
                    var br = document.createElement("br");
                    var small = document.createElement("small");
                    small.textContent = "Rates are fetched every minute. Check back shortly.";
                    span.appendChild(br);
                    span.appendChild(small);
                    emptyCell.appendChild(span);
                    emptyRow.appendChild(emptyCell);
                    tbody.appendChild(emptyRow);
                    return;
                }

                emptyCell.appendChild(alertDiv);
            } else {
                emptyCell.className += " text-muted py-5";
                emptyCell.textContent = "No arbitrage opportunities detected at this time. Rates are fetched every minute. Check back shortly.";
            }

            emptyRow.appendChild(emptyCell);
            tbody.appendChild(emptyRow);
            return;
        }

        var notionalPerLeg = parseFloat(tbody.dataset.notionalPerLeg) || 0;
        var volumeFraction = parseFloat(tbody.dataset.volumeFraction) || 0;

        opportunitiesArr.forEach(function (opp) {
            var aprClass = opp.annualizedYield > 0.50
                ? "text-success fw-bold"
                : opp.annualizedYield >= 0.20
                    ? "text-warning fw-bold"
                    : "";

            var constrained = volumeFraction > 0
                && Math.min(opp.longVolume24h, opp.shortVolume24h) * volumeFraction < notionalPerLeg;

            var row = document.createElement("tr");
            if (constrained) row.className = "table-warning";

            var tdAsset = document.createElement("td");
            var strongEl = document.createElement("strong");
            strongEl.textContent = opp.assetSymbol;
            tdAsset.appendChild(strongEl);
            row.appendChild(tdAsset);

            // Format mark prices as average of long/short
            var tdPrice = document.createElement("td");
            var lp = opp.longMarkPrice || 0;
            var sp = opp.shortMarkPrice || 0;
            var avgPrice = (lp > 0 && sp > 0) ? (lp + sp) / 2 : Math.max(lp, sp);
            tdPrice.textContent = formatPrice(avgPrice);
            row.appendChild(tdPrice);

            var tdLong = document.createElement("td");
            tdLong.textContent = opp.longExchangeName;
            row.appendChild(tdLong);

            var tdShort = document.createElement("td");
            tdShort.textContent = opp.shortExchangeName;
            row.appendChild(tdShort);

            var tdSpread = document.createElement("td");
            tdSpread.className = "text-info";
            tdSpread.textContent = (opp.spreadPerHour * 100).toFixed(4) + "%";
            row.appendChild(tdSpread);

            var tdYield = document.createElement("td");
            tdYield.textContent = ((opp.boostedNetYieldPerHour ?? opp.netYieldPerHour) * 100).toFixed(4) + "%";
            row.appendChild(tdYield);

            var tdApr = document.createElement("td");
            tdApr.className = aprClass;
            var aprText = (opp.annualizedYield * 100).toFixed(1) + "%";
            if (constrained) {
                tdApr.textContent = aprText + " ";
                var warn = document.createElement("span");
                warn.className = "ms-1";
                warn.title = "Position size limited by liquidity";
                warn.textContent = "\u26A0";
                tdApr.appendChild(warn);
            } else {
                tdApr.textContent = aprText;
            }
            row.appendChild(tdApr);

            var tdNext = document.createElement("td");
            tdNext.className = "text-muted small";
            if (opp.minutesToNextSettlement != null) {
                tdNext.textContent = opp.minutesToNextSettlement + "m";
            }
            row.appendChild(tdNext);

            tbody.appendChild(row);
        });
    });

    // Balance update from BalanceAggregator
    connection.on("ReceiveBalanceUpdate", function (snapshot) {
        var row = document.getElementById("exchange-balances-row");
        var container = document.getElementById("exchange-balances");
        var totalEl = document.getElementById("balance-total");
        if (!row || !container) return;

        container.replaceChildren();
        var balances = snapshot.balances || [];

        balances.forEach(function (b) {
            var span = document.createElement("span");
            if (b.errorMessage) {
                span.className = "text-warning balance-error";
                span.title = b.errorMessage;
                span.textContent = b.exchangeName + ": \u26A0 error";
            } else {
                span.className = b.availableUsdc > 0 ? "text-success" : "text-muted";
                span.textContent = b.exchangeName + ": $" + b.availableUsdc.toFixed(2);
            }
            container.appendChild(span);
        });

        if (totalEl) {
            totalEl.textContent = "Total: $" + (snapshot.totalAvailableUsdc || 0).toFixed(2);
        }

        row.style.display = balances.length > 0 ? "block" : "none";
    });

    // B4: Handle status explanations
    connection.on("ReceiveStatusExplanation", function (message, severity) {
        var cssClass = severity === "danger" ? "text-bg-danger"
            : severity === "warning" ? "text-bg-warning"
            : "text-bg-info";

        showToast(message, cssClass, 4000);

        // Update persistent status area
        var statusArea = document.getElementById("status-explanation");
        if (statusArea) {
            var alertClass = severity === "danger" ? "alert-danger"
                : severity === "warning" ? "alert-warning"
                : "alert-info";
            statusArea.className = "alert " + alertClass + " mb-0 py-2 px-3 small";
            statusArea.textContent = message;
            statusArea.style.display = "block";
        }
    });

    // Retry Now button
    var retryBtn = document.getElementById("retry-now-btn");
    if (retryBtn) {
        retryBtn.addEventListener("click", async function () {
            retryBtn.disabled = true;
            retryBtn.textContent = "Triggering...";
            try {
                var token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
                if (!token) {
                    console.warn("CSRF token not found — retry request skipped");
                    retryBtn.textContent = "Retry Now";
                    retryBtn.disabled = false;
                    return;
                }
                var resp = await fetch("/Dashboard/RetryNow", {
                    method: "POST",
                    headers: {
                        "Content-Type": "application/json",
                        "RequestVerificationToken": token
                    }
                });
                var respData = await resp.json();
                if (respData.success) {
                    retryBtn.textContent = "Triggered!";
                    setTimeout(function () { retryBtn.textContent = "Retry Now"; retryBtn.disabled = false; }, 3000);
                }
            } catch (e) {
                retryBtn.textContent = "Retry Now";
                retryBtn.disabled = false;
            }
        });
    }
})();
