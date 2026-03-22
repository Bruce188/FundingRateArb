// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

/**
 * Updates a threshold helper text element with human-readable percentage and APY conversion.
 * The input value is a decimal per-hour funding rate (e.g. 0.0003 = 0.03%/hr).
 * @param {string} inputId  - The id of the threshold <input> element.
 * @param {string} helperId - The id of the <small> helper text element.
 */
function updateThresholdHelper(inputId, helperId) {
    var input = document.getElementById(inputId);
    var helper = document.getElementById(helperId);
    if (!input || !helper) return;

    var val = parseFloat(input.value);
    if (isNaN(val) || val === 0) {
        helper.textContent = '';
        return;
    }
    var pctHour = (val * 100).toFixed(4);
    var apy = (val * 24 * 365 * 100).toFixed(1);
    helper.textContent = '= ' + pctHour + '%/hr | ~' + apy + '% APY';
}

/**
 * Wires a threshold input to its helper element, updating on every keystroke and on page load.
 * @param {string} inputId  - The id of the threshold <input> element.
 * @param {string} helperId - The id of the <small> helper text element.
 */
function wireThresholdHelper(inputId, helperId) {
    var input = document.getElementById(inputId);
    if (!input) return;
    input.addEventListener('input', function () { updateThresholdHelper(inputId, helperId); });
    updateThresholdHelper(inputId, helperId);
}
