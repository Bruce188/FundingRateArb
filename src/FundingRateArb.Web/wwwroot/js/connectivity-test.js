"use strict";

(function () {
    var logPanel = document.getElementById("logPanel");
    var userSelect = document.getElementById("userSelect");
    var btnTestAll = document.getElementById("btnTestAll");
    var btnClearLog = document.getElementById("btnClearLog");
    var testButtons = document.querySelectorAll(".btn-test");
    function getAntiForgeryToken() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";
    }

    // Active exchange IDs for selected user
    var userExchangeIds = new Set();
    var isRunning = false;

    function isScrolledToBottom(el) {
        return el.scrollHeight - el.scrollTop - el.clientHeight < 30;
    }

    function appendLog(exchangeName, message, cssClass) {
        var now = new Date();
        var timestamp = now.toLocaleTimeString("en-GB", { hour12: false });
        var line = document.createElement("span");
        line.setAttribute("style", cssClass || "");
        line.textContent = "[" + timestamp + "] [" + exchangeName + "] " + message + "\n";
        var wasAtBottom = isScrolledToBottom(logPanel);
        logPanel.appendChild(line);
        // Cap log entries to prevent unbounded DOM growth (batch removal to avoid layout thrash)
        var excess = logPanel.childElementCount - 500;
        if (excess > 0) {
            var range = document.createRange();
            range.setStartBefore(logPanel.firstChild);
            range.setEndAfter(logPanel.childNodes[excess - 1]);
            range.deleteContents();
        }
        if (wasAtBottom) {
            logPanel.scrollTop = logPanel.scrollHeight;
        }
    }

    function setButtonState(btn, state) {
        var spinner = btn.querySelector(".spinner-border");
        var icon = btn.querySelector("i");
        var exchangeId = btn.getAttribute("data-exchange-id");
        var statusBadge = document.getElementById("status-" + exchangeId);

        switch (state) {
            case "running":
                btn.disabled = true;
                if (spinner) spinner.classList.remove("d-none");
                if (icon) icon.classList.add("d-none");
                if (statusBadge) {
                    statusBadge.className = "badge bg-warning mb-3 exchange-status";
                    statusBadge.textContent = "Running...";
                }
                break;
            case "pass":
                btn.disabled = false;
                if (spinner) spinner.classList.add("d-none");
                if (icon) icon.classList.remove("d-none");
                if (statusBadge) {
                    statusBadge.className = "badge bg-success mb-3 exchange-status";
                    statusBadge.textContent = "Pass";
                }
                break;
            case "fail":
                btn.disabled = false;
                if (spinner) spinner.classList.add("d-none");
                if (icon) icon.classList.remove("d-none");
                if (statusBadge) {
                    statusBadge.className = "badge bg-danger mb-3 exchange-status";
                    statusBadge.textContent = "Fail";
                }
                break;
            case "idle":
                btn.disabled = false;
                if (spinner) spinner.classList.add("d-none");
                if (icon) icon.classList.remove("d-none");
                if (statusBadge) {
                    statusBadge.className = "badge bg-secondary mb-3 exchange-status";
                    statusBadge.textContent = "Idle";
                }
                break;
        }
    }

    function updateButtonAvailability() {
        testButtons.forEach(function (btn) {
            var exchangeId = parseInt(btn.getAttribute("data-exchange-id"), 10);
            var hasCredentials = userExchangeIds.has(exchangeId);
            var userSelected = userSelect.value !== "";
            btn.disabled = !userSelected || !hasCredentials || isRunning;
        });
        btnTestAll.disabled = !userSelect.value || isRunning || userExchangeIds.size === 0;
    }

    // Fetch user's exchanges when user selection changes
    userSelect.addEventListener("change", function () {
        var userId = userSelect.value;

        // Reset all statuses
        testButtons.forEach(function (btn) { setButtonState(btn, "idle"); });
        userExchangeIds = new Set();

        if (!userId) {
            updateButtonAvailability();
            return;
        }

        fetch("/Admin/ConnectivityTest/GetUserExchanges?userId=" + encodeURIComponent(userId))
            .then(function (res) {
                if (!res.ok) throw new Error("Server returned " + res.status);
                return res.json();
            })
            .then(function (exchangeIds) {
                userExchangeIds = new Set(exchangeIds);
                updateButtonAvailability();
            })
            .catch(function (err) {
                appendLog("System", "Failed to load user exchanges: " + err.message, "color: #f44336;");
            });
    });

    // Run test for a single exchange
    function runTest(btn) {
        var exchangeId = btn.getAttribute("data-exchange-id");
        var exchangeName = btn.getAttribute("data-exchange-name");
        var userId = userSelect.value;

        if (!userId) return Promise.resolve();

        var dryRunInput = document.getElementById("dryRunInput");
        var dryRunValue = dryRunInput ? dryRunInput.value : "true";

        if (dryRunValue === "false") {
            var confirmed = window.confirm(
                "This will place a real $10 order on mainnet using your API credentials.\nProceed?"
            );
            if (!confirmed) return Promise.resolve();
        }

        setButtonState(btn, "running");
        appendLog(exchangeName, "Starting connectivity test...", "color: #64b5f6;");

        var formData = new FormData();
        formData.append("userId", userId);
        formData.append("exchangeId", exchangeId);
        formData.append("dryRun", dryRunValue);
        formData.append("__RequestVerificationToken", getAntiForgeryToken());

        return fetch("/Admin/ConnectivityTest/RunTest", {
            method: "POST",
            body: formData
        })
        .then(function (res) {
            if (!res.ok) throw new Error("Server returned " + res.status);
            return res.json();
        })
        .then(function (data) {
            var badge = document.getElementById("modeBadge");
            if (badge && data.mode) {
                if (data.mode === "DryRun") {
                    badge.textContent = "DRY RUN";
                    badge.className = "badge bg-success ms-2";
                } else if (data.mode === "LiveTrade") {
                    badge.textContent = "LIVE TEST";
                    badge.className = "badge bg-warning text-dark ms-2";
                }
            }
            if (data.success) {
                setButtonState(btn, "pass");
                appendLog(exchangeName, "TEST PASSED", "color: #4caf50; font-weight: bold;");
            } else {
                setButtonState(btn, "fail");
                appendLog(exchangeName, "TEST FAILED: " + (data.error || "Unknown error"), "color: #f44336; font-weight: bold;");
            }
        })
        .catch(function (err) {
            setButtonState(btn, "fail");
            appendLog(exchangeName, "Request error: " + err.message, "color: #f44336;");
        });
    }

    // Individual test button clicks
    testButtons.forEach(function (btn) {
        btn.addEventListener("click", function () {
            if (isRunning) return;
            isRunning = true;
            updateButtonAvailability();
            runTest(btn).finally(function () {
                isRunning = false;
                updateButtonAvailability();
            });
        });
    });

    // Test All button — wrapped in try/finally to guarantee isRunning reset
    btnTestAll.addEventListener("click", async function () {
        if (isRunning) return;
        isRunning = true;
        updateButtonAvailability();

        try {
            appendLog("System", "Starting test for all exchanges...", "color: #64b5f6; font-weight: bold;");

            var enabledButtons = [];
            testButtons.forEach(function (btn) {
                var exchangeId = parseInt(btn.getAttribute("data-exchange-id"), 10);
                if (userExchangeIds.has(exchangeId)) {
                    enabledButtons.push(btn);
                }
            });

            for (var i = 0; i < enabledButtons.length; i++) {
                await runTest(enabledButtons[i]);
            }

            appendLog("System", "All tests complete.", "color: #64b5f6; font-weight: bold;");
        } finally {
            isRunning = false;
            updateButtonAvailability();
        }
    });

    // Clear log
    btnClearLog.addEventListener("click", function () {
        logPanel.replaceChildren();
    });

    // SignalR handler for real-time log messages
    if (window.appSignalR && window.appSignalR.connection) {
        window.appSignalR.connection.on("ReceiveConnectivityLog", function (exchangeName, message) {
            // Determine color based on message content
            var cssClass = "color: #d4d4d4;";
            if (message.indexOf("PASS") >= 0 || message.indexOf("SUCCESS") >= 0) {
                cssClass = "color: #4caf50;";
            } else if (message.indexOf("FAIL") >= 0 || message.indexOf("failed") >= 0 || message.indexOf("error") >= 0 || message.indexOf("Error") >= 0) {
                cssClass = "color: #f44336;";
            } else if (message.indexOf("Step") >= 0 || message.indexOf("Starting") >= 0 || message.indexOf("Waiting") >= 0) {
                cssClass = "color: #64b5f6;";
            }
            appendLog(exchangeName, message, cssClass);
        });
    }

    // Initial state
    updateButtonAvailability();
})();
