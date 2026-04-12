/**
 * Rewrites all <time class="local-time"> elements to display in the user's local timezone.
 * Reads the ISO-8601 `datetime` attribute, formats via Intl.DateTimeFormat, and replaces
 * the element's textContent. Gracefully handles invalid datetime values by leaving the
 * server-rendered text in place.
 */
function rewriteLocalTimes() {
    var elements = document.querySelectorAll("time.local-time");
    for (var i = 0; i < elements.length; i++) {
        var el = elements[i];
        var dt = el.getAttribute("datetime");
        if (!dt) continue;
        try {
            var date = new Date(dt);
            if (isNaN(date.getTime())) continue;
            var fmt = new Intl.DateTimeFormat(undefined, {
                year: "numeric", month: "2-digit", day: "2-digit",
                hour: "2-digit", minute: "2-digit",
                timeZoneName: "short"
            });
            el.textContent = fmt.format(date);
        } catch (e) {
            // leave server-rendered text in place
        }
    }
}
document.addEventListener("DOMContentLoaded", rewriteLocalTimes);
