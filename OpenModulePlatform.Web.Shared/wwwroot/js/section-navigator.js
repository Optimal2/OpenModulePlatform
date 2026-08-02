// File: OpenModulePlatform.Web.Shared/wwwroot/js/section-navigator.js
(function () {
    "use strict";

    var storageKey = "omp.section-navigator.width";
    var minWidth = 140;
    var maxWidth = 420;

    // Narrow mode: layouts with a shared pane keep the splitter below the
    // stacking breakpoint. The pane collapses to the left edge (width 0) by
    // default - as if the handle had been dragged fully left - and drags
    // below the snap width settle back into the collapsed state. The pulled-
    // out width lives in sessionStorage so it survives navigation but every
    // new session starts collapsed.
    var narrowQuery = window.matchMedia("(max-width: 1500px)");
    var narrowStorageKey = "omp.section-navigator.narrow-width";
    var narrowCollapseSnap = 80;
    var narrowMaxWidth = 360;
    var narrowDefaultWidth = 240;

    function isNarrowPaneLayout(layout) {
        return narrowQuery.matches && !!layout.querySelector(".section-navigator-pane");
    }

    function clampWidth(value, narrow) {
        return narrow
            ? Math.min(narrowMaxWidth, Math.max(0, value))
            : Math.min(maxWidth, Math.max(minWidth, value));
    }

    function readNarrowWidth() {
        try {
            var parsed = parseInt(window.sessionStorage.getItem(narrowStorageKey) || "", 10);
            return Number.isFinite(parsed) ? parsed : null;
        } catch (error) {
            return null;
        }
    }

    function storeNarrowWidth(value) {
        try {
            if (value === null) {
                window.sessionStorage.removeItem(narrowStorageKey);
            } else {
                window.sessionStorage.setItem(narrowStorageKey, String(value));
            }
        } catch (error) {
            // Storage may be unavailable; the state still applies for the page.
        }
    }

    function readStoredWidth() {
        try {
            var parsed = parseInt(window.localStorage.getItem(storageKey) || "", 10);
            return Number.isFinite(parsed) ? clampWidth(parsed, false) : null;
        } catch (error) {
            return null;
        }
    }

    function applyNarrowState(layout) {
        if (!layout || !layout.querySelector(".section-navigator-pane")) {
            return;
        }

        if (narrowQuery.matches) {
            var narrowWidth = readNarrowWidth();
            if (narrowWidth !== null && narrowWidth >= narrowCollapseSnap) {
                layout.classList.remove("section-navigator-layout--pane-collapsed");
                applyWidth(layout, clampWidth(narrowWidth, true));
            } else {
                layout.classList.add("section-navigator-layout--pane-collapsed");
                applyWidth(layout, 0);
            }
        } else {
            layout.classList.remove("section-navigator-layout--pane-collapsed");
            applyWidth(layout, readStoredWidth());
        }

        updateLayoutMode(layout);
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

    // Switches the layout between the centered base grid and the full-width
    // splitter grid: full when the pane no longer fits its equal share beside
    // the centered content. Hysteresis applies only mid-drag, so the mode
    // cannot flap while the handle hovers at the boundary but always settles
    // on the exact rule at init, window resize and drag end.
    function updateLayoutMode(layout, useHysteresis) {
        if (!layout) {
            return;
        }

        var pane = layout.querySelector(".section-navigator-pane, .section-navigator, .omp-linkbox");
        if (!pane) {
            return;
        }

        var styles = window.getComputedStyle(layout);
        var contentMax = parseFloat(styles.getPropertyValue("--section-navigator-content-max")) || 1400;
        var gap = parseFloat(styles.getPropertyValue("--section-navigator-gap")) || 16;
        var paneWidth = pane.getBoundingClientRect().width;
        if (pane.classList.contains("section-navigator-pane")) {
            // The pane's scrollport padding extends into the column gap.
            paneWidth -= gap;
        }

        var centerShare = (layout.clientWidth - (gap * 2) - contentMax) / 2;
        var wasFull = layout.classList.contains("section-navigator-layout--full");
        var full = paneWidth > centerShare;
        if (useHysteresis && wasFull && !full && paneWidth > centerShare - 24) {
            full = true;
        }

        layout.classList.toggle("section-navigator-layout--full", full);
    }

    // Logical width of the resized element: the pane's scrollport padding
    // extends into the column gap and must not count as pane width, or drags
    // would jump and store 16px too much.
    function measureTargetWidth(target) {
        var width = target.getBoundingClientRect().width;
        if (target.classList.contains("section-navigator-pane")) {
            width -= parseFloat(window.getComputedStyle(target).paddingRight) || 0;
        }

        return width;
    }

    function snapGrip(handle) {
        // Grid layout can place the handle on a fractional pixel (odd viewport
        // widths), which antialiases the thin edge lines asymmetrically.
        // Measure and publish a whole-pixel delta for the line pseudo-elements.
        var rect = handle.getBoundingClientRect();
        var centerX = rect.left + (rect.width / 2);
        handle.style.setProperty("--section-navigator-grip-dx", (Math.round(centerX) - centerX) + "px");
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
            dragStartWidth = measureTargetWidth(target);
            handle.setPointerCapture(event.pointerId);
            document.body.classList.add("section-navigator-resizing");
            // Pulling out a collapsed pane: reveal the content immediately so
            // the drag gives visual feedback; the end-of-drag snap decides
            // whether it stays out.
            if (layout.classList.contains("section-navigator-layout--pane-collapsed")) {
                layout.classList.remove("section-navigator-layout--pane-collapsed");
                dragStartWidth = 0;
            }
        });

        handle.addEventListener("pointermove", function (event) {
            if (!handle.hasPointerCapture(event.pointerId)) {
                return;
            }

            applyWidth(layout, clampWidth(Math.round(dragStartWidth + (event.clientX - dragStartX)), isNarrowPaneLayout(layout)));
            updateLayoutMode(layout, true);
            snapGrip(handle);
        });

        function endDrag(event) {
            if (!handle.hasPointerCapture(event.pointerId)) {
                return;
            }

            handle.releasePointerCapture(event.pointerId);
            document.body.classList.remove("section-navigator-resizing");
            if (isNarrowPaneLayout(layout)) {
                var narrowWidth = Math.round(measureTargetWidth(target));
                if (narrowWidth < narrowCollapseSnap) {
                    storeNarrowWidth(null);
                } else {
                    storeNarrowWidth(clampWidth(narrowWidth, true));
                }
                applyNarrowState(layout);
            } else {
                storeWidth(clampWidth(Math.round(measureTargetWidth(target)), false));
                updateLayoutMode(layout, false);
            }
            snapGrip(handle);
        }

        handle.addEventListener("pointerup", endDrag);
        handle.addEventListener("pointercancel", endDrag);

        handle.addEventListener("dblclick", function () {
            if (isNarrowPaneLayout(layout)) {
                // Double-click toggles the collapsed state at a comfortable width.
                var collapsed = layout.classList.contains("section-navigator-layout--pane-collapsed");
                storeNarrowWidth(collapsed ? narrowDefaultWidth : null);
                applyNarrowState(layout);
            } else {
                applyWidth(layout, null);
                storeWidth(null);
                updateLayoutMode(layout);
            }
            snapGrip(handle);
        });

        updateLayoutMode(layout);
        snapGrip(handle);
    }

    // Refresh every handle's layout mode and grip. Registered once on window
    // resize/load (not per handle) so the listeners never accumulate when a
    // page has more than one resize handle.
    function refreshAllHandles() {
        document.querySelectorAll("[data-section-navigator-resize]").forEach(function (handle) {
            var layout = handle.closest(".section-navigator-layout");
            if (layout) {
                updateLayoutMode(layout);
            }

            snapGrip(handle);
        });
    }

    // Pane elements (link boxes, the section navigator) collapse via their
    // heading; the state persists per collapse key (box key or page path) so
    // what an operator folded stays folded across visits. Toggling changes
    // the pane geometry, so grip snaps are refreshed afterwards.
    function initPaneCollapse() {
        document.querySelectorAll("details[data-omp-pane-collapse]").forEach(function (details) {
            var paneKey = details.getAttribute("data-omp-pane-collapse");
            if (!paneKey) {
                return;
            }

            var collapseKey = "omp.pane-collapsed." + paneKey;
            try {
                if (window.localStorage.getItem(collapseKey) === "1") {
                    details.open = false;
                }
            } catch (error) {
                // Storage may be unavailable; the element still collapses for the page.
            }

            details.addEventListener("toggle", function () {
                try {
                    if (details.open) {
                        window.localStorage.removeItem(collapseKey);
                    } else {
                        window.localStorage.setItem(collapseKey, "1");
                    }
                } catch (error) {
                    // Ignore storage failures.
                }

                document.querySelectorAll("[data-section-navigator-resize]").forEach(snapGrip);
            });
        });
    }

    function init() {
        initPaneCollapse();

        // One full-height grab edge per shared pane: on wide screens it just
        // widens the draggable surface (its lines stay hidden there), on
        // narrow screens it is what remains of a collapsed pane.
        document.querySelectorAll(".section-navigator-pane").forEach(function (pane) {
            if (pane.querySelector(".section-navigator-pane__edge")) {
                return;
            }

            var edge = document.createElement("div");
            edge.className = "section-navigator__resize-handle section-navigator-pane__edge";
            edge.setAttribute("data-section-navigator-resize", "");
            edge.setAttribute("aria-hidden", "true");
            pane.appendChild(edge);
        });

        var handles = Array.from(document.querySelectorAll("[data-section-navigator-resize]"));
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

        document.querySelectorAll(".section-navigator-layout").forEach(applyNarrowState);
        narrowQuery.addEventListener("change", function () {
            document.querySelectorAll(".section-navigator-layout").forEach(applyNarrowState);
            document.querySelectorAll("[data-section-navigator-resize]").forEach(snapGrip);
        });

        // Late layout shifts (web fonts, images) can move the anchor after init.
        window.addEventListener("resize", refreshAllHandles);
        window.addEventListener("load", refreshAllHandles);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
}());
