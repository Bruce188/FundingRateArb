"use strict";

// Dashboard-specific SignalR event handlers.
// Depends on signalr-connection.js which provides window.appSignalR.

(function () {
    if (!window.appSignalR) return;
    var connection = window.appSignalR.connection;
    var showToast = window.appSignalR.showToast;

    // Row-click navigation: navigate to position details on click/Enter
    function bindRowClick(row) {
        if (!row || row.dataset.rowClickBound) return;
        row.dataset.rowClickBound = "1";
        function navigate() {
            window.location.href = "/Positions/Details/" + row.dataset.positionId;
        }
        row.addEventListener("click", function(e) {
            if (e.target.closest("a, button")) return;
            navigate();
        });
        row.addEventListener("keydown", function(e) {
            if (e.key === "Enter") navigate();
        });
    }

    function bindRowClickHandlers(container) {
        if (!container) return;
        var rows = container.querySelectorAll("tr[data-position-id]");
        for (var i = 0; i < rows.length; i++) {
            bindRowClick(rows[i]);
        }
    }

    // Bind row-click on page load for server-rendered rows
    document.addEventListener("DOMContentLoaded", function() {
        var posTable = document.getElementById("positions-table");
        if (posTable) {
            var tbody = posTable.querySelector("tbody");
            bindRowClickHandlers(tbody);
        }
    });

    var lastUpdateTime = null;
    var staleTimer = null;
    var STALE_THRESHOLD_MS = 120000; // 2 minutes

    // Dynamic decimal formatting for small prices
    function formatPrice(price) {
        if (price === 0) return "$0.00";
        if (price >= 1) return "$" + price.toFixed(2);
        if (price >= 0.01) return "$" + price.toFixed(4);
        return "$" + parseFloat(price.toFixed(8)).toPrecision(4);
    }

    connection.on("ReceiveDashboardUpdate", function (data) {
        // Track last update for staleness detection
        lastUpdateTime = Date.now();
        var staleIndicator = document.getElementById("stale-indicator");
        if (staleIndicator) {
            staleIndicator.style.display = "none";
        }
        var lastUpdateEl = document.getElementById("last-update-time");
        if (lastUpdateEl) {
            lastUpdateEl.textContent = new Date().toLocaleTimeString();
        }
        if (staleTimer) clearTimeout(staleTimer);
        staleTimer = setTimeout(function () {
            var indicator = document.getElementById("stale-indicator");
            if (indicator) indicator.style.display = "";
        }, STALE_THRESHOLD_MS);

        var botStatus = document.getElementById("bot-status");
        if (botStatus) {
            var state = (data.operatingState || "Stopped").toUpperCase();
            var colorMap = { "STOPPED": "bg-danger", "PAUSED": "bg-warning", "ARMED": "bg-primary", "TRADING": "bg-success" };
            botStatus.textContent = state;
            botStatus.className = "badge " + (colorMap[state] || "bg-secondary");
        }

        var openPositions = document.getElementById("open-positions");
        if (openPositions) openPositions.textContent = data.openPositionCount;

        var totalPnl = document.getElementById("total-pnl");
        if (totalPnl) totalPnl.textContent = (data.totalPnl ?? 0).toFixed(4);

        var openingPositions = document.getElementById("opening-positions");
        if (openingPositions) openingPositions.textContent = data.openingPositionCount || 0;

        var needsAttention = document.getElementById("needs-attention");
        if (needsAttention) {
            var count = data.needsAttentionCount || 0;
            needsAttention.textContent = count;
            var badge = document.getElementById("needs-attention-badge");
            if (badge) {
                badge.classList.remove("text-warning", "text-success");
                badge.classList.add(count > 0 ? "text-warning" : "text-success");
            }
        }

        var bestSpread = document.getElementById("best-spread");
        if (bestSpread) {
            if (!data.botEnabled && (data.bestSpread ?? 0) <= 0) {
                bestSpread.textContent = "Bot off";
            } else {
                var spread = data.bestSpread ?? 0;
                if (spread > 0) {
                    bestSpread.textContent = (spread * 100).toFixed(4) + "%";
                }
                // When spread <= 0 and bot is enabled, preserve current display value
            }
        }
    });

    var iconMap = { 0: "\u25B2", 1: "\u2248", 2: "\u23F0", 3: "\u26BF", 4: "\u2193", 5: "\u25CE" };
    var nameMap = { 0: "SpreadRisk", 1: "Liquidity", 2: "TimeBased", 3: "Leverage", 4: "Loss", 5: "PnlProgress" };

    function applyWarningIcons(container, warnTypes) {
        var existing = container.querySelectorAll(".warning-icon");
        existing.forEach(function (el) { el.remove(); });
        (warnTypes || []).forEach(function (wt) {
            var span = document.createElement("span");
            span.className = "warning-icon warning-" + (nameMap[wt] || "").toLowerCase();
            span.title = nameMap[wt] || "";
            span.textContent = iconMap[wt] || "";
            container.appendChild(span);
        });
    }

    function applyWarningRowClass(el, warnLevel) {
        el.classList.remove("table-danger", "table-warning", "table-info", "border-danger", "border-warning", "border-info");
        if (warnLevel === 3) el.classList.add("table-danger");
        else if (warnLevel === 2) el.classList.add("table-warning");
        else if (warnLevel === 1) el.classList.add("table-info");
    }

    function createPositionRow(position) {
        var tr = document.createElement("tr");
        tr.id = "position-" + position.id;
        tr.setAttribute("data-position-id", position.id);
        tr.setAttribute("tabindex", "0");
        tr.setAttribute("role", "link");
        tr.style.cursor = "pointer";
        var spread = position.currentSpreadPerHour ?? 0;
        var spreadClass = spread >= 0 ? "text-success" : "text-danger";

        var tdAsset = document.createElement("td");
        var strong = document.createElement("strong");
        strong.textContent = position.assetSymbol || "?";
        tdAsset.appendChild(strong);
        applyWarningIcons(tdAsset, position.warningTypes);
        tr.appendChild(tdAsset);

        var cols = [
            position.longExchangeName || "?",
            position.shortExchangeName || "?",
            "$" + (position.sizeUsdc ?? 0).toFixed(2),
            (((position.entrySpreadPerHour ?? 0) * 100).toFixed(4)) + "%"
        ];
        cols.forEach(function (text) {
            var td = document.createElement("td");
            td.textContent = text;
            tr.appendChild(td);
        });

        var tdSpread = document.createElement("td");
        tdSpread.className = "position-spread " + spreadClass;
        tdSpread.textContent = (spread * 100).toFixed(6) + "%";
        tr.appendChild(tdSpread);

        var unifiedPnl = position.unifiedPnl ?? 0;
        var exchangePnl = position.exchangePnl ?? 0;
        var funding = position.accumulatedFunding ?? 0;

        var tdStrategyPnl = document.createElement("td");
        tdStrategyPnl.className = "position-pnl " + (unifiedPnl >= 0 ? "text-success" : "text-danger");
        tdStrategyPnl.textContent = "$" + unifiedPnl.toFixed(4);
        tr.appendChild(tdStrategyPnl);

        var tdExchPnl = document.createElement("td");
        tdExchPnl.className = "position-exchange-pnl " + (exchangePnl >= 0 ? "text-success" : "text-danger");
        var divergence = position.divergencePct ?? 0;
        var exchTextNode = document.createTextNode("$" + exchangePnl.toFixed(4) + " ");
        tdExchPnl.appendChild(exchTextNode);
        var badge = document.createElement("span");
        badge.className = "badge bg-info ms-1 divergence-badge";
        badge.title = "Cross-exchange price divergence (|longMark - shortMark| / unified)";
        badge.textContent = divergence.toFixed(2) + "%";
        badge.style.display = divergence > 0.01 ? "" : "none";
        tdExchPnl.appendChild(badge);
        tr.appendChild(tdExchPnl);

        var tdFunding = document.createElement("td");
        tdFunding.className = funding >= 0 ? "text-success" : "text-danger";
        tdFunding.textContent = "$" + funding.toFixed(4);
        tr.appendChild(tdFunding);

        var tdTime = document.createElement("td");
        var openedValue = position.confirmedAtUtc || position.openedAt;
        if (openedValue) {
            var time = document.createElement("time");
            time.className = "local-time";
            time.setAttribute("datetime", openedValue);
            time.textContent = new Date(openedValue).toLocaleString();
            tdTime.appendChild(time);
        }
        tr.appendChild(tdTime);

        applyWarningRowClass(tr, position.warningLevel ?? 0);
        return tr;
    }

    function createPositionCard(position) {
        var card = document.createElement("div");
        card.className = "card mb-2 mobile-card";
        card.id = "position-card-" + position.id;
        var warnLevel = position.warningLevel ?? 0;
        if (warnLevel === 3) card.classList.add("border-danger");
        else if (warnLevel === 2) card.classList.add("border-warning");
        else if (warnLevel === 1) card.classList.add("border-info");

        var body = document.createElement("div");
        body.className = "card-body p-2";

        var headerRow = document.createElement("div");
        headerRow.className = "d-flex justify-content-between";
        var assetStrong = document.createElement("strong");
        assetStrong.textContent = position.assetSymbol || "?";
        headerRow.appendChild(assetStrong);
        var warningSpan = document.createElement("span");
        applyWarningIcons(warningSpan, position.warningTypes);
        headerRow.appendChild(warningSpan);
        body.appendChild(headerRow);

        var exchangeSmall = document.createElement("small");
        exchangeSmall.className = "text-muted";
        exchangeSmall.textContent = (position.longExchangeName || "?") + " \u2192 " + (position.shortExchangeName || "?");
        body.appendChild(exchangeSmall);

        var detailDiv = document.createElement("div");
        detailDiv.className = "small";
        var spread = position.currentSpreadPerHour ?? 0;
        detailDiv.textContent = "Size: $" + (position.sizeUsdc ?? 0).toFixed(2) + " | Spread: " + (spread * 100).toFixed(6) + "%/hr";
        body.appendChild(detailDiv);

        var pnlDiv = document.createElement("div");
        pnlDiv.className = "small";
        var cardUnified = position.unifiedPnl ?? 0;
        var cardExch = position.exchangePnl ?? 0;

        var stratLabel = document.createTextNode("Strategy: ");
        pnlDiv.appendChild(stratLabel);
        var stratSpan = document.createElement("span");
        stratSpan.className = cardUnified >= 0 ? "text-success" : "text-danger";
        stratSpan.textContent = "$" + cardUnified.toFixed(4);
        pnlDiv.appendChild(stratSpan);

        var sep = document.createTextNode(" | Exch: ");
        pnlDiv.appendChild(sep);
        var exchSpan = document.createElement("span");
        exchSpan.className = cardExch >= 0 ? "text-success" : "text-danger";
        exchSpan.textContent = "$" + cardExch.toFixed(4);
        pnlDiv.appendChild(exchSpan);

        body.appendChild(pnlDiv);

        card.appendChild(body);
        return card;
    }

    connection.on("ReceivePositionUpdate", function (position) {
        // N1: Validate position.id is numeric before using in DOM IDs
        var safeId = parseInt(position.id, 10);
        if (isNaN(safeId)) return;

        var positionRow = document.getElementById("position-" + safeId);
        if (positionRow) {
            var pnlEl = positionRow.querySelector(".position-pnl");
            if (pnlEl) {
                var uPnl = position.unifiedPnl ?? 0;
                pnlEl.textContent = "$" + uPnl.toFixed(4);
                pnlEl.className = "position-pnl " + (uPnl >= 0 ? "text-success" : "text-danger");
            }
            var exchPnlEl = positionRow.querySelector(".position-exchange-pnl");
            if (exchPnlEl) {
                var ePnl = position.exchangePnl ?? 0;
                var div = position.divergencePct ?? 0;
                exchPnlEl.className = "position-exchange-pnl " + (ePnl >= 0 ? "text-success" : "text-danger");

                // Update text node without destroying child elements
                var textNode = exchPnlEl.firstChild;
                if (textNode && textNode.nodeType === 3) {
                    textNode.textContent = "$" + ePnl.toFixed(4) + " ";
                }

                // Reuse pre-created badge, toggle visibility
                var dbadge = exchPnlEl.querySelector(".divergence-badge");
                if (dbadge) {
                    dbadge.textContent = div.toFixed(2) + "%";
                    dbadge.style.display = div > 0.01 ? "" : "none";
                }
            }
            var spreadEl = positionRow.querySelector(".position-spread");
            if (spreadEl) {
                spreadEl.textContent = ((position.currentSpreadPerHour ?? 0) * 100).toFixed(6) + "%";
            }

            applyWarningRowClass(positionRow, position.warningLevel ?? 0);

            // Update warning type icons in the first cell
            var firstTd = positionRow.querySelector("td:first-child");
            if (firstTd) {
                applyWarningIcons(firstTd, position.warningTypes);
            }
        } else {
            // NB6: Ignore updates for closed positions
            if (position.status === "Closed" || position.status === "EmergencyClosed") return;

            // NB6: Dedup guard — re-check after creation race
            if (document.getElementById("position-" + safeId)) return;

            // New position — reveal section if hidden and create DOM elements
            var section = document.getElementById("positions-section");
            if (section) {
                section.classList.remove("d-none");
            }
            var emptyMsg = document.getElementById("positions-empty");
            if (emptyMsg) {
                emptyMsg.classList.add("d-none");
            }

            var posTable = document.getElementById("positions-table");
            var tbody = posTable ? posTable.querySelector("tbody") : null;
            if (tbody) {
                var newRow = createPositionRow(position);
                tbody.appendChild(newRow);
                bindRowClick(newRow);
            }
            // Mobile card
            var cardsContainer = document.getElementById("positions-cards");
            var newCard = null;
            if (cardsContainer) {
                newCard = createPositionCard(position);
                cardsContainer.appendChild(newCard);
            }
            // Rewrite local times for newly added elements only
            if (typeof rewriteLocalTimes === "function") {
                if (newRow) { rewriteLocalTimes(newRow); }
                if (newCard) { rewriteLocalTimes(newCard); }
            }
        }
    });

    connection.on("ReceivePositionRemoval", function (positionId) {
        var parsedId = parseInt(positionId, 10);
        if (isNaN(parsedId)) return;
        var positionRow = document.getElementById("position-" + parsedId);
        if (positionRow) {
            positionRow.remove();
        }
        // Also remove the mobile card variant
        var positionCard = document.getElementById("position-card-" + parsedId);
        if (positionCard) {
            positionCard.remove();
        }

        // Hide section if no positions remain (check both table and cards)
        var posTable = document.getElementById("positions-table");
        var tbody = posTable ? posTable.querySelector("tbody") : null;
        var cardsContainer = document.getElementById("positions-cards");
        var tableEmpty = !tbody || tbody.children.length === 0;
        var cardsEmpty = !cardsContainer || cardsContainer.children.length === 0;
        if (tableEmpty && cardsEmpty) {
            var section = document.getElementById("positions-section");
            if (section) {
                section.classList.add("d-none");
            }
            var emptyMsg = document.getElementById("positions-empty");
            if (emptyMsg) {
                emptyMsg.classList.remove("d-none");
            }
        }
        // Open position count is updated authoritatively by ReceiveDashboardUpdate
    });

    var isMobile = window.matchMedia('(max-width: 575.98px)');

    // C1 fix: replaced innerHTML with createElement + textContent for opportunity data
    connection.on("ReceiveOpportunityUpdate", function (data) {
        // Display circuit breaker warnings
        var cbContainer = document.getElementById("circuit-breaker-status");
        if (cbContainer) {
            cbContainer.replaceChildren();
            var breakers = data.circuitBreakers || [];
            if (breakers.length > 0) {
                breakers.forEach(function (cb) {
                    var alert = document.createElement("div");
                    alert.className = "alert alert-warning mb-2 py-2 d-flex align-items-center";
                    var icon = document.createElement("i");
                    icon.className = "bi bi-exclamation-triangle-fill me-2";
                    alert.appendChild(icon);
                    var text = document.createElement("span");
                    text.textContent = cb.exchangeName + " circuit breaker active" +
                        (cb.remainingMinutes > 0 ? " (resumes in " + cb.remainingMinutes + "m)" : "");
                    alert.appendChild(text);
                    cbContainer.appendChild(alert);
                });
            }
        }

        // Update circuit breaker health panel list-group
        var cbPanel = document.getElementById("circuit-breaker-panel");
        if (cbPanel) {
            var cbList = cbPanel.querySelector(".list-group");
            var cbHealthMsg = cbPanel.querySelector("p.text-success");
            var breakers = data.circuitBreakers || [];
            if (breakers.length === 0) {
                if (cbList) cbList.remove();
                if (!cbHealthMsg) {
                    var p = document.createElement("p");
                    p.className = "text-success mb-0 small";
                    p.textContent = "All exchanges healthy.";
                    var panelBody = cbPanel.querySelector(".card-body");
                    if (panelBody) panelBody.appendChild(p);
                }
            } else {
                if (cbHealthMsg) cbHealthMsg.remove();
                var panelBody = cbPanel.querySelector(".card-body");
                if (panelBody) {
                    var ul = panelBody.querySelector(".list-group") || document.createElement("ul");
                    ul.className = "list-group list-group-flush";
                    ul.replaceChildren();
                    breakers.forEach(function (cb) {
                        var li = document.createElement("li");
                        li.className = "list-group-item d-flex justify-content-between align-items-center px-0 py-1";
                        var nameSpan = document.createElement("span");
                        nameSpan.className = "small";
                        nameSpan.textContent = cb.exchangeName;
                        li.appendChild(nameSpan);
                        var badge = document.createElement("span");
                        badge.className = "badge bg-danger";
                        badge.textContent = "Open \u2014 " + (cb.remainingMinutes || 0) + " min";
                        li.appendChild(badge);
                        ul.appendChild(li);
                    });
                    if (!panelBody.querySelector(".list-group")) {
                        panelBody.appendChild(ul);
                    }
                }
            }
        }

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

        // Update Best Spread KPI: prefer live opportunity data, fall back to diagnostics
        var bestFromOpps = opportunitiesArr.length > 0
            ? Math.max.apply(null, opportunitiesArr.map(function(o) { return o.spreadPerHour ?? 0; }))
            : (diagnostics && diagnostics.bestRawSpread) || 0;
        var bestSpreadEl = document.getElementById("best-spread");
        if (bestSpreadEl) {
            if (bestFromOpps > 0) {
                bestSpreadEl.textContent = (bestFromOpps * 100).toFixed(4) + "%";
            } else {
                bestSpreadEl.textContent = "N/A";
            }
        }

        tbody.replaceChildren();
        if (opportunitiesArr.length === 0) {
            var emptyRow = document.createElement("tr");
            var emptyCell = document.createElement("td");
            emptyCell.colSpan = 12;
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
                } else if (diagnostics.pairsPassing === 0 && (diagnostics.pairsFilteredByThreshold > 0 || diagnostics.netPositiveBelowThreshold > 0 || diagnostics.netPositiveBelowEdgeGuardrail > 0 || diagnostics.pairsFilteredByBreakEvenSize > 0)) {
                    alertDiv.className += " alert alert-info";
                    var totalRejected = (diagnostics.pairsFilteredByThreshold || 0) + (diagnostics.netPositiveBelowThreshold || 0) + (diagnostics.netPositiveBelowEdgeGuardrail || 0) + (diagnostics.pairsFilteredByBreakEvenSize || 0);
                    alertDiv.textContent = totalRejected + " pairs rejected by profitability filters.";

                    var belowThreshold = (diagnostics.pairsFilteredByThreshold || 0) + (diagnostics.netPositiveBelowThreshold || 0);
                    if (belowThreshold > 0) {
                        alertDiv.appendChild(document.createElement("br"));
                        var thresholdBold = document.createElement("strong");
                        thresholdBold.textContent = belowThreshold + " below " + (diagnostics.openThreshold * 100).toFixed(3) + "% net yield threshold";
                        alertDiv.appendChild(thresholdBold);
                        alertDiv.appendChild(document.createTextNode("."));
                    }
                    if (diagnostics.netPositiveBelowThreshold > 0) {
                        var adaptiveBold = document.createElement("strong");
                        adaptiveBold.textContent = " " + diagnostics.netPositiveBelowThreshold + " net-positive below gate (adaptive eligible).";
                        alertDiv.appendChild(adaptiveBold);
                    }
                    if (diagnostics.pairsFilteredByBreakEvenSize > 0) {
                        alertDiv.appendChild(document.createElement("br"));
                        var beBold = document.createElement("strong");
                        beBold.textContent = diagnostics.pairsFilteredByBreakEvenSize + " filtered by break-even-size floor";
                        alertDiv.appendChild(beBold);
                        alertDiv.appendChild(document.createTextNode(" \u2014 net \u00d7 MinHoldTimeHours below MinEdgeMultiplier \u00d7 fees."));
                    }
                    if (diagnostics.netPositiveBelowEdgeGuardrail > 0) {
                        alertDiv.appendChild(document.createElement("br"));
                        var guardrailBold = document.createElement("strong");
                        guardrailBold.textContent = diagnostics.netPositiveBelowEdgeGuardrail + " above threshold but below the 3\u00d7 edge guardrail";
                        alertDiv.appendChild(guardrailBold);
                        alertDiv.appendChild(document.createTextNode(" \u2014 loosen MinEdgeMultiplier to surface."));
                    }
                    alertDiv.appendChild(document.createElement("br"));
                    alertDiv.appendChild(document.createTextNode("Best raw spread: " + (diagnostics.bestRawSpread * 100).toFixed(4) + "%."));
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

            // Col 1: Asset (with optional CoinGlass hot badge)
            var tdAsset = document.createElement("td");
            var strongEl = document.createElement("strong");
            strongEl.textContent = opp.assetSymbol;
            tdAsset.appendChild(strongEl);
            if (opp.isCoinGlassHot) {
                var cgBadge = document.createElement("span");
                cgBadge.className = "badge bg-info ms-1";
                cgBadge.title = "Matching cross-exchange arbitrage opportunity on CoinGlass";
                cgBadge.textContent = "CG";
                tdAsset.appendChild(cgBadge);
            }
            row.appendChild(tdAsset);

            // Col 2: Mark Price (avg of long/short)
            var tdPrice = document.createElement("td");
            var lp = opp.longMarkPrice || 0;
            var sp = opp.shortMarkPrice || 0;
            var avgPrice = (lp > 0 && sp > 0) ? (lp + sp) / 2 : Math.max(lp, sp);
            tdPrice.textContent = formatPrice(avgPrice);
            row.appendChild(tdPrice);

            // Col 3: Long Exchange
            var tdLong = document.createElement("td");
            tdLong.textContent = opp.longExchangeName;
            row.appendChild(tdLong);

            // Col 4: Short Exchange
            var tdShort = document.createElement("td");
            tdShort.textContent = opp.shortExchangeName;
            row.appendChild(tdShort);

            // Col 5: Spread/hr
            var tdSpread = document.createElement("td");
            tdSpread.className = "text-info";
            tdSpread.textContent = (opp.spreadPerHour * 100).toFixed(4) + "%";
            row.appendChild(tdSpread);

            // Col 6: Net Yield/hr (boosted)
            var tdYield = document.createElement("td");
            tdYield.textContent = ((opp.boostedNetYieldPerHour ?? opp.netYieldPerHour) * 100).toFixed(4) + "%";
            row.appendChild(tdYield);

            // Col 7: Predicted (value + trend arrow + confidence dot)
            var tdPredicted = document.createElement("td");
            if (opp.predictedSpread != null) {
                var predValueSpan = document.createElement("span");
                predValueSpan.textContent = (opp.predictedSpread * 100).toFixed(4) + "%";
                tdPredicted.appendChild(predValueSpan);

                var trend = opp.predictedTrend || "n/a";
                var arrow = trend === "rising" ? "^" : trend === "falling" ? "v" : "-";
                var arrowClass = trend === "rising" ? "text-success" : trend === "falling" ? "text-danger" : "text-muted";
                var arrowSpan = document.createElement("span");
                arrowSpan.className = "trend-arrow " + arrowClass + " ms-1";
                arrowSpan.title = "Trend: " + trend;
                arrowSpan.textContent = arrow;
                tdPredicted.appendChild(arrowSpan);

                var conf = opp.predictionConfidence || 0;
                var confClass = conf >= 0.7 ? "text-success" : conf >= 0.4 ? "text-warning" : "text-danger";
                var confSpan = document.createElement("span");
                confSpan.className = "prediction-confidence " + confClass;
                confSpan.title = "Confidence: " + (conf * 100).toFixed(0) + "%";
                tdPredicted.appendChild(confSpan);
            } else {
                var predDash = document.createElement("span");
                predDash.className = "text-muted";
                predDash.textContent = "\u2014";
                tdPredicted.appendChild(predDash);
            }
            row.appendChild(tdPredicted);

            // Col 8: APR (volume-constrained opportunities are flagged via the row's table-warning class)
            var tdApr = document.createElement("td");
            tdApr.className = aprClass;
            tdApr.textContent = (opp.annualizedYield * 100).toFixed(1) + "%";
            row.appendChild(tdApr);

            // Col 9: Lev (effective leverage)
            var tdLev = document.createElement("td");
            tdLev.className = "text-muted small";
            if (opp.effectiveLeverage != null) {
                tdLev.textContent = opp.effectiveLeverage + "x";
            } else {
                tdLev.textContent = "\u2014";
            }
            row.appendChild(tdLev);

            // Col 10: ROC APR (return on capital, leverage-adjusted)
            var tdRoc = document.createElement("td");
            tdRoc.className = "small";
            if (opp.aprOnCapital != null) {
                var rocClass = opp.aprOnCapital > 100 ? "text-success fw-bold"
                    : opp.aprOnCapital > 50 ? "text-warning fw-bold" : "";
                var rocSpan = document.createElement("span");
                rocSpan.className = rocClass;
                rocSpan.textContent = opp.aprOnCapital.toFixed(0) + "%";
                tdRoc.appendChild(rocSpan);
            } else {
                var rocDash = document.createElement("span");
                rocDash.className = "text-muted";
                rocDash.textContent = "\u2014";
                tdRoc.appendChild(rocDash);
            }
            row.appendChild(tdRoc);

            // Col 11: BE Cyc (break-even cycles)
            var tdBeCyc = document.createElement("td");
            tdBeCyc.className = "text-muted small";
            if (opp.breakEvenCycles != null) {
                tdBeCyc.textContent = opp.breakEvenCycles.toFixed(1);
            } else {
                tdBeCyc.textContent = "\u2014";
            }
            row.appendChild(tdBeCyc);

            // Col 12: Next (minutes to next funding settlement)
            var tdNext = document.createElement("td");
            tdNext.className = "text-muted small";
            if (opp.minutesToNextSettlement != null) {
                tdNext.textContent = opp.minutesToNextSettlement + "m";
            } else {
                tdNext.textContent = "\u2014";
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
        // Balance tile is always visible (server-rendered). No show/hide toggle needed.
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

    // Clear stale timer on connection close
    connection.onclose(function () {
        if (staleTimer) clearTimeout(staleTimer);
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
