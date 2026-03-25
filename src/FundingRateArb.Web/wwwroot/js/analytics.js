"use strict";

// Analytics pages — real-time updates via shared SignalR connection.
// Depends on signalr-connection.js which provides window.appSignalR.

(function () {
    if (!window.appSignalR) return;
    var connection = window.appSignalR.connection;

    // Rate Analytics: live chart data point on ReceiveFundingRateUpdate
    // Only binds if the rate chart canvas exists on the current page.
    // NB2: Robust guard — verify Chart.js is loaded, canvas exists, chart is initialized,
    // and datasets are present before attempting to update.
    connection.on("ReceiveFundingRateUpdate", function (rateData) {
        if (typeof Chart === "undefined") return;

        var chartCanvas = document.getElementById("rateChart");
        if (!chartCanvas) return;

        var chart = Chart.getChart(chartCanvas);
        if (!chart || !chart.data || !chart.data.datasets || chart.data.datasets.length === 0) return;

        // Validate incoming data before appending
        if (!rateData || !rateData.timestamp || rateData.rate === undefined || rateData.rate === null) return;

        var label = new Date(rateData.timestamp).toLocaleTimeString();
        chart.data.labels.push(label);
        chart.data.datasets[0].data.push(rateData.rate);

        // Keep a rolling window of 100 data points
        if (chart.data.labels.length > 100) {
            chart.data.labels.shift();
            chart.data.datasets[0].data.shift();
        }

        chart.update("none");
    });

    // Passed Opportunities: live opportunity count update
    connection.on("ReceiveOpportunityUpdate", function (data) {
        var totalEl = document.getElementById("opp-total-seen");
        if (totalEl) {
            var current = parseInt(totalEl.textContent) || 0;
            var newCount = (data.opportunities || []).length;
            totalEl.textContent = current + newCount;
        }
    });
})();
