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

    function initHandle(handle) {
        // The measured element is the shared pane when one exists (so the
        // width covers both the navigator and the link box), otherwise the
        // component the handle belongs to.
        var target = handle.closest(".section-navigator-pane")
            || handle.closest(".section-navigator")
            || handle.closest(".omp-linkbox");
        var layout = handle.closest(".section-navigator-layout");
        if (!target || !layout) {
            return;
        }

        var dragStartX = 0;
        var dragStartWidth = 0;

        handle.addEventListener("pointerdown", function (event) {
            if (!event.isPrimary) {
                return;
            }

            event.preventDefault();
            dragStartX = event.clientX;
            dragStartWidth = target.getBoundingClientRect().width;
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
            storeWidth(clampWidth(Math.round(target.getBoundingClientRect().width)));
        }

        handle.addEventListener("pointerup", endDrag);
        handle.addEventListener("pointercancel", endDrag);

        handle.addEventListener("dblclick", function () {
            applyWidth(layout, null);
            storeWidth(null);
        });
    }

    function init() {
        var handles = Array.prototype.slice.call(document.querySelectorAll("[data-section-navigator-resize]"));
        if (handles.length === 0) {
            return;
        }

        var storedWidth = readStoredWidth();
        if (storedWidth !== null) {
            var layout = document.querySelector(".section-navigator-layout");
            if (layout) {
                applyWidth(layout, storedWidth);
            }
        }

        handles.forEach(initHandle);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
}());
