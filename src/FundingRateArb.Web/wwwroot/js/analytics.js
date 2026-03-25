"use strict";

// Analytics pages — real-time updates via shared SignalR connection.
// Depends on signalr-connection.js which provides window.appSignalR.

(function () {
    if (!window.appSignalR) return;
    var connection = window.appSignalR.connection;

    // Rate Analytics: live chart data point on ReceiveFundingRateUpdate
    // Only binds if a Chart.js instance exists on the page
    connection.on("ReceiveFundingRateUpdate", function (rateData) {
        var chartCanvas = document.getElementById("rateChart");
        if (!chartCanvas) return;

        // Chart.js stores instance on canvas via Chart.getChart()
        var chart = typeof Chart !== "undefined" ? Chart.getChart(chartCanvas) : null;
        if (!chart) return;

        // Append new data point if the chart has datasets
        if (chart.data.datasets.length > 0 && rateData.timestamp && rateData.rate !== undefined) {
            var label = new Date(rateData.timestamp).toLocaleTimeString();
            chart.data.labels.push(label);
            chart.data.datasets[0].data.push(rateData.rate);

            // Keep a rolling window of 100 data points
            if (chart.data.labels.length > 100) {
                chart.data.labels.shift();
                chart.data.datasets[0].data.shift();
            }

            chart.update("none");
        }
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
