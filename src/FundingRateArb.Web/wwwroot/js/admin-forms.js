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

/**
 * BPS input binding for BotConfig admin form.
 * Provides a user-friendly BPS input that translates to/from the raw decimal
 * stored in the hidden form field (e.g., a visible value is stored as visible * factor).
 *
 * @param {string} visibleId - ID of the visible number input
 * @param {string} rawId - ID of the hidden input bound to the viewmodel
 * @param {number} [factor=10000] - Conversion factor (default 10000 for bps)
 */
function bindBpsInput(visibleId, rawId, factor) {
    if (factor === undefined) factor = 10000;
    var visInput = document.getElementById(visibleId);
    var rawInput = document.getElementById(rawId);
    if (!visInput || !rawInput) return;

    // On load: visible = raw / factor
    var rawVal = parseFloat(rawInput.value);
    if (!isNaN(rawVal)) {
        visInput.value = (rawVal / factor).toFixed(10).replace(/\.?0+$/, '');
    }

    // On input/change: raw = visible * factor; NaN → leave raw unchanged
    function handleChange() {
        var visVal = parseFloat(visInput.value);
        if (!isNaN(visVal)) {
            rawInput.value = (visVal * factor).toFixed(8).replace(/\.?0+$/, '');
        }
    }

    visInput.addEventListener('input', handleChange);
    visInput.addEventListener('change', handleChange);
}
