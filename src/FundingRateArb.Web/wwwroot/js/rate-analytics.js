// Rate Analytics — Chart.js rendering for trends and time-of-day patterns
(function () {
    'use strict';

    var configEl = document.getElementById('rate-analytics-config');
    if (!configEl) return;

    var assetIdRaw = configEl.getAttribute('data-asset-id');
    var exchangeOptionsEl = document.getElementById('exchange-options-json');
    var config = {
        assetId: assetIdRaw ? parseInt(assetIdRaw, 10) : null,
        days: parseInt(configEl.getAttribute('data-days'), 10) || 7,
        exchangeOptions: exchangeOptionsEl ? JSON.parse(exchangeOptionsEl.textContent) : []
    };
    if (!config.assetId || config.exchangeOptions.length === 0) return;

    var chartColors = ['#0dcaf0', '#ffc107', '#198754', '#dc3545', '#6f42c1', '#fd7e14'];

    // ── Trend Chart (F6: loaded via AJAX) ─────────────────────────
    var trendCanvas = document.getElementById('rateTrendChart');
    if (trendCanvas) {
        // F16: Use URLSearchParams for defensive URL construction
        var trendParams = new URLSearchParams({
            assetId: config.assetId,
            days: config.days
        });
        fetch('/Analytics/RateTrendData?' + trendParams.toString())
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (trends) {
                var datasets = trends.map(function (trend, idx) {
                    return {
                        label: trend.exchangeName,
                        data: trend.hourlyRates.map(function (pt) {
                            return { x: pt.hourUtc, y: pt.avgRate };
                        }),
                        borderColor: chartColors[idx % chartColors.length],
                        backgroundColor: 'transparent',
                        borderWidth: 1.5,
                        pointRadius: 0,
                        tension: 0.3,
                    };
                });

                new Chart(trendCanvas.getContext('2d'), {
                    type: 'line',
                    data: { datasets: datasets },
                    options: {
                        responsive: true,
                        interaction: { mode: 'index', intersect: false },
                        scales: {
                            x: {
                                type: 'time',
                                time: { unit: config.days <= 3 ? 'hour' : 'day', tooltipFormat: 'MMM d, HH:mm' },
                                grid: { color: 'rgba(255,255,255,0.1)' },
                                ticks: { color: '#aaa' }
                            },
                            y: {
                                grid: { color: 'rgba(255,255,255,0.1)' },
                                ticks: { color: '#aaa', callback: function (v) { return v.toFixed(6); } }
                            }
                        },
                        plugins: {
                            legend: { labels: { color: '#ccc' } }
                        }
                    }
                });
            })
            .catch(function (err) {
                console.error('Failed to load trend data:', err);
            });
    }

    // ── Time-of-Day Chart ────────────────────────────────────────
    var todCanvas = document.getElementById('todChart');
    var todSelector = document.getElementById('tod-exchange-selector');
    var todChart = null;

    function loadTimeOfDay(exchangeId) {
        // F16: Use URLSearchParams for defensive URL construction
        var params = new URLSearchParams({
            assetId: config.assetId,
            exchangeId: exchangeId,
            days: config.days
        });
        fetch('/Analytics/TimeOfDayData?' + params.toString())
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                if (todChart) todChart.destroy();

                var labels = data.map(function (d) { return d.hourUtc + ':00'; });
                var values = data.map(function (d) { return d.avgRate; });

                todChart = new Chart(todCanvas.getContext('2d'), {
                    type: 'bar',
                    data: {
                        labels: labels,
                        datasets: [{
                            label: 'Avg Rate by Hour (UTC)',
                            data: values,
                            backgroundColor: values.map(function (v) {
                                return v >= 0 ? 'rgba(25,135,84,0.7)' : 'rgba(220,53,69,0.7)';
                            }),
                        }]
                    },
                    options: {
                        responsive: true,
                        indexAxis: 'y',
                        scales: {
                            x: {
                                grid: { color: 'rgba(255,255,255,0.1)' },
                                ticks: { color: '#aaa', callback: function (v) { return v.toFixed(6); } }
                            },
                            y: {
                                grid: { color: 'rgba(255,255,255,0.1)' },
                                ticks: { color: '#aaa' }
                            }
                        },
                        plugins: {
                            legend: { labels: { color: '#ccc' } }
                        }
                    }
                });
            })
            .catch(function (err) {
                console.error('Failed to load time-of-day data:', err);
            });
    }

    if (todCanvas && todSelector) {
        todSelector.addEventListener('change', function () {
            loadTimeOfDay(this.value);
        });
        // Load initial
        loadTimeOfDay(todSelector.value);
    }

    // ── Z-Score Alerts (lazy-loaded via AJAX) ─────────────────────
    var alertsCard = document.getElementById('zscore-alerts-card');
    var alertsBody = document.getElementById('zscore-alerts-body');

    function loadZScoreAlerts() {
        fetch('/Analytics/ZScoreAlerts?threshold=2.0')
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (alerts) {
                if (!alerts || alerts.length === 0) {
                    alertsCard.style.display = 'none';
                    return;
                }
                alertsBody.innerHTML = '';
                alerts.forEach(function (a) {
                    var absZ = Math.abs(a.zScore);
                    var badgeClass = absZ > 3 ? 'bg-danger' : 'bg-warning text-dark';
                    var row = '<tr>'
                        + '<td>' + a.assetSymbol + '</td>'
                        + '<td>' + a.exchangeName + '</td>'
                        + '<td>' + a.currentRate.toFixed(6) + '</td>'
                        + '<td>' + a.mean7d.toFixed(6) + '</td>'
                        + '<td>' + a.stdDev7d.toFixed(6) + '</td>'
                        + '<td><span class="badge ' + badgeClass + '">' + a.zScore.toFixed(2) + '</span></td>'
                        + '</tr>';
                    alertsBody.insertAdjacentHTML('beforeend', row);
                });
                alertsCard.style.display = '';
            })
            .catch(function (err) {
                console.error('Failed to load Z-score alerts:', err);
            });
    }

    if (alertsCard && alertsBody) {
        loadZScoreAlerts();
        // Auto-refresh every 60 seconds
        setInterval(loadZScoreAlerts, 60000);
    }
})();
