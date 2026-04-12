/**
 * Fractional-percent input binding for BotConfig admin form.
 * Provides a user-friendly percentage input that translates to/from the raw decimal
 * stored in the hidden form field (e.g., 0.02% displayed as user types 0.02,
 * stored as 0.0002 in the hidden input).
 *
 * @param {string} percentInputId - ID of the visible percentage number input
 * @param {string} rawHiddenInputId - ID of the hidden input bound to the viewmodel
 * @param {number} multiplier - Conversion factor (100 for percent, 10000 for bps)
 */
function bindPercentInput(percentInputId, rawHiddenInputId, multiplier) {
    var pctInput = document.getElementById(percentInputId);
    var rawInput = document.getElementById(rawHiddenInputId);
    if (!pctInput || !rawInput) return;

    // Find or create the raw-value badge
    var badge = pctInput.parentElement.querySelector('.raw-value-badge');
    if (!badge) {
        badge = document.createElement('small');
        badge.className = 'raw-value-badge text-muted d-block';
        pctInput.parentElement.appendChild(badge);
    }

    function updateBadge() {
        var rawVal = parseFloat(rawInput.value);
        if (!isNaN(rawVal)) {
            badge.textContent = '(raw: ' + rawVal + ')';
        } else {
            badge.textContent = '';
        }
    }

    // On load: compute pct = raw * multiplier
    var rawVal = parseFloat(rawInput.value);
    if (!isNaN(rawVal)) {
        pctInput.value = (rawVal * multiplier).toFixed(6).replace(/\.?0+$/, '');
    }
    updateBadge();

    // On change: raw = pct / multiplier
    pctInput.addEventListener('input', function () {
        var pctVal = parseFloat(pctInput.value);
        if (!isNaN(pctVal)) {
            rawInput.value = (pctVal / multiplier).toFixed(8).replace(/\.?0+$/, '');
        } else {
            rawInput.value = '';
        }
        updateBadge();
    });
}
