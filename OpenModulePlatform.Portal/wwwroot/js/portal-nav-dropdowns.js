(function () {
    var canHoverMedia = typeof window.matchMedia === "function"
        ? window.matchMedia("(hover: hover) and (pointer: fine)")
        : null;

    function canHover() {
        return !!(canHoverMedia && canHoverMedia.matches);
    }

    function getDropdowns() {
        return Array.from(document.querySelectorAll(".nav-dropdown"));
    }

    function isPinned(dropdown) {
        return dropdown.dataset.pinned === "true";
    }

    function setPinned(dropdown, value) {
        dropdown.dataset.pinned = value ? "true" : "false";
    }

    function closeDropdown(dropdown) {
        if (!dropdown) {
            return;
        }

        dropdown.open = false;
        setPinned(dropdown, false);
    }

    function openDropdown(dropdown, pinned) {
        if (!dropdown) {
            return;
        }

        closeAll(dropdown);
        dropdown.open = true;
        setPinned(dropdown, !!pinned);
    }

    function closeAll(except) {
        getDropdowns().forEach(function (dropdown) {
            if (dropdown !== except) {
                closeDropdown(dropdown);
            }
        });
    }

    function initDropdown(dropdown) {
        if (!dropdown || dropdown.dataset.dropdownInit === "true") {
            return;
        }

        dropdown.dataset.dropdownInit = "true";
        setPinned(dropdown, false);

        var summary = dropdown.querySelector("summary");
        if (!summary) {
            return;
        }

        summary.addEventListener("click", function (event) {
            event.preventDefault();

            var shouldOpenPinned = !dropdown.open || !isPinned(dropdown);
            if (shouldOpenPinned) {
                openDropdown(dropdown, true);
            } else {
                closeDropdown(dropdown);
            }
        });

        dropdown.addEventListener("mouseenter", function () {
            if (!canHover() || isPinned(dropdown)) {
                return;
            }

            openDropdown(dropdown, false);
        });

        dropdown.addEventListener("mouseleave", function () {
            if (!canHover() || isPinned(dropdown)) {
                return;
            }

            closeDropdown(dropdown);
        });
    }

    function initAll() {
        getDropdowns().forEach(initDropdown);
    }

    document.addEventListener("click", function (event) {
        getDropdowns().forEach(function (dropdown) {
            if (!dropdown.contains(event.target)) {
                closeDropdown(dropdown);
            }
        });
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            closeAll(null);
        }
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initAll, { once: true });
    } else {
        initAll();
    }
})();
