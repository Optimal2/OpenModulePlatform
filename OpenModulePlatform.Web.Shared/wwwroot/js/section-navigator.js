// File: OpenModulePlatform.Web.Shared/wwwroot/js/section-navigator.js
(function () {
    "use strict";

    var storageKey = "omp.section-navigator.width";
    var minWidth = 140;
    var maxWidth = 420;

    function clampWidth(value) {
        return Math.min(maxWidth, Math.max(minWidth, value));
    }

    function readStoredWidth() {
        try {
            var parsed = parseInt(window.localStorage.getItem(storageKey) || "", 10);
            return Number.isFinite(parsed) ? clampWidth(parsed) : null;
        } catch (error) {
            return null;
        }
    }

    function storeWidth(value) {
        try {
            if (value === null) {
                window.localStorage.removeItem(storageKey);
            } else {
                window.localStorage.setItem(storageKey, String(value));
            }
        } catch (error) {
            // Storage may be unavailable; resizing still works for the current page.
        }
    }

    function applyWidth(layout, value) {
        if (value === null) {
            layout.style.removeProperty("--section-navigator-pane-width");
        } else {
            layout.style.setProperty("--section-navigator-pane-width", value + "px");
        }
    }

    function init() {
        var handle = document.querySelector("[data-section-navigator-resize]");
        var navigator = handle ? handle.closest(".section-navigator") : null;
        var layout = navigator ? navigator.closest(".section-navigator-layout") : null;
        if (!handle || !navigator || !layout) {
            return;
        }

        var storedWidth = readStoredWidth();
        if (storedWidth !== null) {
            applyWidth(layout, storedWidth);
        }

        var dragStartX = 0;
        var dragStartWidth = 0;

        handle.addEventListener("pointerdown", function (event) {
            if (!event.isPrimary) {
                return;
            }

            event.preventDefault();
            dragStartX = event.clientX;
            dragStartWidth = navigator.getBoundingClientRect().width;
            handle.setPointerCapture(event.pointerId);
            document.body.classList.add("section-navigator-resizing");
        });

        handle.addEventListener("pointermove", function (event) {
            if (!handle.hasPointerCapture(event.pointerId)) {
                return;
            }

            applyWidth(layout, clampWidth(Math.round(dragStartWidth + (event.clientX - dragStartX))));
        });

        function endDrag(event) {
            if (!handle.hasPointerCapture(event.pointerId)) {
                return;
            }

            handle.releasePointerCapture(event.pointerId);
            document.body.classList.remove("section-navigator-resizing");
            storeWidth(clampWidth(Math.round(navigator.getBoundingClientRect().width)));
        }

        handle.addEventListener("pointerup", endDrag);
        handle.addEventListener("pointercancel", endDrag);

        handle.addEventListener("dblclick", function () {
            applyWidth(layout, null);
            storeWidth(null);
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
}());
