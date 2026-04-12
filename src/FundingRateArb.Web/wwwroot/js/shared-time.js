/**
 * Rewrites all <time class="local-time"> elements to display in the user's local timezone.
 * Reads the ISO-8601 `datetime` attribute, formats via Intl.DateTimeFormat, and replaces
 * the element's textContent. Gracefully handles invalid datetime values by leaving the
 * server-rendered text in place.
 */
function rewriteLocalTimes(container) {
    var root = container || document;
    var elements = root.querySelectorAll("time.local-time:not([data-local-done])");
    if (elements.length === 0) return;
    var fmt = new Intl.DateTimeFormat(undefined, {
        year: "numeric", month: "2-digit", day: "2-digit",
        hour: "2-digit", minute: "2-digit",
        timeZoneName: "short"
    });
    for (var i = 0; i < elements.length; i++) {
        var el = elements[i];
        var dt = el.getAttribute("datetime");
        if (!dt) continue;
        try {
            var date = new Date(dt);
            if (isNaN(date.getTime())) continue;
            el.textContent = fmt.format(date);
            el.setAttribute("data-local-done", "1");
        } catch (e) {
            // leave server-rendered text in place
        }
    }
}
document.addEventListener("DOMContentLoaded", rewriteLocalTimes);
