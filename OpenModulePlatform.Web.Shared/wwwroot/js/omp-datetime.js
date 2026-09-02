// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-datetime.js
// Shared date & time picker: progressive enhancement over a plain input.
// Pages write <input data-omp-datetime="date|time|datetime" name="..."
// value="ISO"> and the enhancement moves the name to a hidden input that
// keeps the canonical ISO value, turns the visible input into a masked
// digit-by-digit field, and adds a popup with a calendar and a time grid.
// All values are treated as wall-clock text (the UTC convention lives in
// the labels); "Now"/"Today" insert the current UTC time on purpose.
(function () {
    "use strict";

    var texts = (document.documentElement.lang || "").toLowerCase().indexOf("sv") === 0
        ? { time: "Tid", clear: "Rensa", reset: "Återställ", now: "Nu", today: "I dag", week: "v.", open: "Öppna kalendern", close: "Stäng kalendern", year: "åååå" }
        : { time: "Time", clear: "Clear", reset: "Reset", now: "Now", today: "Today", week: "wk", open: "Open the calendar", close: "Close the calendar", year: "yyyy" };

    // The year placeholder follows the page language; its four letters keep
    // the slot positions identical across languages. Segments are digit-index
    // ranges (year, month, day, hour, minute) - the field selects and moves
    // by whole segments like the native datetime input.
    var templates = {
        date: { display: texts.year + "-mm-dd", slots: [0, 1, 2, 3, 5, 6, 8, 9], segments: [[0, 3], [4, 5], [6, 7]] },
        time: { display: "--:--", slots: [0, 1, 3, 4], segments: [[0, 1], [2, 3]] },
        datetime: { display: texts.year + "-mm-dd --:--", slots: [0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15], segments: [[0, 3], [4, 5], [6, 7], [8, 9], [10, 11]] }
    };

    function segmentAt(st, digitIndex) {
        var segments = st.template.segments;
        for (var i = 0; i < segments.length; i++) {
            if (digitIndex >= segments[i][0] && digitIndex <= segments[i][1]) { return segments[i]; }
        }
        return segments[segments.length - 1];
    }

    var panel = null;
    var panelInput = null;
    var panelToggle = null;

    function pad(value) { return String(value).padStart(2, "0"); }

    function monthNames() {
        var formatter = new Intl.DateTimeFormat(document.documentElement.lang || undefined, { month: "long" });
        var names = [];
        for (var month = 0; month < 12; month++) {
            names.push(formatter.format(new Date(Date.UTC(2024, month, 1))));
        }
        return names;
    }

    function weekdayNames() {
        var formatter = new Intl.DateTimeFormat(document.documentElement.lang || undefined, { weekday: "short" });
        var names = [];
        // 2024-01-01 is a Monday.
        for (var day = 1; day <= 7; day++) {
            names.push(formatter.format(new Date(Date.UTC(2024, 0, day))).slice(0, 2));
        }
        return names;
    }

    function isoWeek(date) {
        var utc = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
        var day = (utc.getUTCDay() + 6) % 7;
        utc.setUTCDate(utc.getUTCDate() - day + 3);
        var firstThursday = new Date(Date.UTC(utc.getUTCFullYear(), 0, 4));
        var firstDay = (firstThursday.getUTCDay() + 6) % 7;
        firstThursday.setUTCDate(firstThursday.getUTCDate() - firstDay + 3);
        return 1 + Math.round((utc - firstThursday) / 604800000);
    }

    function state(input) { return input._ompDatetime; }

    function canonicalFromDigits(st) {
        var d = st.digits;
        if (d.every(function (ch) { return ch === ""; })) { return ""; }
        if (d.some(function (ch) { return ch === ""; })) { return null; }
        if (st.mode === "time") { return d[0] + d[1] + ":" + d[2] + d[3]; }
        var date = d[0] + d[1] + d[2] + d[3] + "-" + d[4] + d[5] + "-" + d[6] + d[7];
        var year = +(d[0] + d[1] + d[2] + d[3]);
        var month = +(d[4] + d[5]);
        var day = +(d[6] + d[7]);
        var check = new Date(Date.UTC(year, month - 1, day));
        if (check.getUTCFullYear() !== year || check.getUTCMonth() !== month - 1 || check.getUTCDate() !== day) {
            return null;
        }
        if (st.mode === "date") { return date; }
        return date + "T" + d[8] + d[9] + ":" + d[10] + d[11];
    }

    function digitsFromCanonical(st, value) {
        var digits = value ? value.replace(/[^0-9]/g, "").split("") : [];
        st.digits = st.template.slots.map(function (_, index) { return digits[index] || ""; });
    }

    function render(st) {
        var display = st.template.display.split("");
        st.template.slots.forEach(function (position, index) {
            if (st.digits[index] !== "") { display[position] = st.digits[index]; }
        });
        st.input.value = display.join("");
        // null means partial or impossible (e.g. Feb 30); all-empty is "".
        st.input.classList.toggle("omp-datetime__input--invalid", canonicalFromDigits(st) === null);
        if (st.clear) {
            st.clear.hidden = !(st.digits.some(function (ch) { return ch !== ""; }) || st.hidden.value !== "");
        }
    }

    // The whole segment the cursor sits in is selected, so the field reads
    // as year/month/day/time parts rather than sixteen loose characters.
    function highlight(st) {
        var index = Math.min(st.cursor, st.template.slots.length - 1);
        var segment = segmentAt(st, index);
        var start = st.template.slots[segment[0]];
        var end = st.template.slots[segment[1]] + 1;
        try { st.input.setSelectionRange(start, end); } catch (error) { /* unfocused */ }
    }

    function commit(st) {
        var canonical = canonicalFromDigits(st);
        if (canonical !== null && canonical !== st.hidden.value) {
            st.hidden.value = canonical;
            st.hidden.dispatchEvent(new Event("change", { bubbles: true }));
        }
        render(st);
        if (panelInput === st.input && panel) { renderPanel(st); }
    }

    function slotAllows(st, index, digit) {
        var mode = st.mode;
        var offset = mode === "time" ? -8 : 0;
        var logical = index + (mode === "time" ? 8 : 0);
        var d = function (i) { return st.digits[i + offset] || ""; };
        switch (logical) {
            case 4: return digit <= 1;
            case 5: return d(4) === "1" ? digit <= 2 : (d(4) === "0" ? digit >= 1 : true);
            case 6: return digit <= 3;
            case 7: return d(6) === "3" ? digit <= 1 : (d(6) === "0" ? digit >= 1 : true);
            case 8: return digit <= 2;
            case 9: return d(8) === "2" ? digit <= 3 : true;
            case 10: return digit <= 5;
            default: return true;
        }
    }

    function onKeydown(event) {
        var st = state(event.target);
        if (!st || event.target.disabled) { return; }

        if (event.key >= "0" && event.key <= "9") {
            event.preventDefault();
            if (slotAllows(st, st.cursor, +event.key)) {
                st.digits[st.cursor] = event.key;
                st.cursor = Math.min(st.cursor + 1, st.template.slots.length - 1);
                if (st.digits.every(function (ch) { return ch !== ""; })) { commit(st); } else { render(st); }
                highlight(st);
            }
            return;
        }

        if (event.key === "Backspace") {
            // Clears the current segment; on an already empty segment it
            // steps to the previous one instead (native-input feel).
            event.preventDefault();
            var segment = segmentAt(st, st.cursor);
            var hasDigit = false;
            for (var i = segment[0]; i <= segment[1]; i++) { if (st.digits[i] !== "") { hasDigit = true; } }
            if (!hasDigit && segment[0] > 0) {
                segment = segmentAt(st, segment[0] - 1);
            }
            for (var j = segment[0]; j <= segment[1]; j++) { st.digits[j] = ""; }
            st.cursor = segment[0];
            render(st);
            highlight(st);
            return;
        }

        if (event.key === "Delete") {
            event.preventDefault();
            st.digits = st.template.slots.map(function () { return ""; });
            st.cursor = 0;
            st.hidden.value = "";
            st.hidden.dispatchEvent(new Event("change", { bubbles: true }));
            render(st);
            highlight(st);
            return;
        }

        if (event.key === "ArrowLeft") {
            event.preventDefault();
            var current = segmentAt(st, st.cursor);
            if (current[0] > 0) { st.cursor = segmentAt(st, current[0] - 1)[0]; }
            highlight(st);
            return;
        }
        if (event.key === "ArrowRight") {
            event.preventDefault();
            var here = segmentAt(st, st.cursor);
            if (here[1] < st.template.slots.length - 1) { st.cursor = here[1] + 1; }
            highlight(st);
            return;
        }
        if (event.key === "Escape") { digitsFromCanonical(st, st.hidden.value); st.cursor = 0; render(st); closePanel(); return; }
        if (event.key === "Tab" || event.key === "Enter") {
            if (event.key === "Enter") { commit(st); }
            return;
        }
        if (event.key.length === 1 && !event.ctrlKey && !event.metaKey) { event.preventDefault(); }
    }

    function onBlurCommit(st) {
        // A partial entry reverts to the last committed value instead of
        // posting garbage; a complete entry commits.
        if (canonicalFromDigits(st) === null) {
            digitsFromCanonical(st, st.hidden.value);
        }
        commit(st);
    }

    function setDatePart(st, year, month, day) {
        var text = String(year).padStart(4, "0") + pad(month) + pad(day);
        for (var i = 0; i < 8; i++) { st.digits[i] = text[i]; }
        if (st.mode === "datetime" && st.digits.slice(8).some(function (ch) { return ch === ""; })) {
            st.digits[8] = "0"; st.digits[9] = "0"; st.digits[10] = "0"; st.digits[11] = "0";
        }
        commit(st);
    }

    function setTimePart(st, hour, minute) {
        var offset = st.mode === "time" ? 0 : 8;
        var text = (hour !== null ? pad(hour) : null);
        if (text !== null) { st.digits[offset] = text[0]; st.digits[offset + 1] = text[1]; }
        if (minute !== null) { var mm = pad(minute); st.digits[offset + 2] = mm[0]; st.digits[offset + 3] = mm[1]; }
        if (st.mode === "datetime" && st.digits.slice(0, 8).some(function (ch) { return ch === ""; })) {
            var now = new Date();
            var t = String(now.getUTCFullYear()) + pad(now.getUTCMonth() + 1) + pad(now.getUTCDate());
            for (var i = 0; i < 8; i++) { st.digits[i] = t[i]; }
        }
        commit(st);
    }

    function selectedParts(st) {
        var canonical = canonicalFromDigits(st);
        var digits = st.digits;
        return {
            year: digits[0] !== "" && digits[3] !== "" ? +(digits[0] + digits[1] + digits[2] + digits[3]) : null,
            month: st.mode !== "time" && digits[4] !== "" && digits[5] !== "" ? +(digits[4] + digits[5]) : null,
            day: st.mode !== "time" && digits[6] !== "" && digits[7] !== "" ? +(digits[6] + digits[7]) : null,
            hour: st.mode !== "date" ? (function () { var o = st.mode === "time" ? 0 : 8; return digits[o] !== "" && digits[o + 1] !== "" ? +(digits[o] + digits[o + 1]) : null; })() : null,
            minute: st.mode !== "date" ? (function () { var o = st.mode === "time" ? 2 : 10; return digits[o] !== "" && digits[o + 1] !== "" ? +(digits[o] + digits[o + 1]) : null; })() : null,
            complete: canonical !== null && canonical !== ""
        };
    }

    // A "datetime" input starts date-only and flips to full datetime via the
    // Time chip in the panel. Off means the value simply has no time part
    // (midnight); the declared mode never changes, only the working mode.
    function setTimeVisible(st, visible) {
        if (st.declaredMode !== "datetime" || (st.mode === "datetime") === visible) { return; }
        if (visible) {
            st.mode = "datetime";
            st.template = templates.datetime;
            st.digits = st.digits.slice(0, 8).concat(["", "", "", ""]);
            if (st.digits.slice(0, 8).every(function (ch) { return ch !== ""; })) {
                st.digits[8] = "0"; st.digits[9] = "0"; st.digits[10] = "0"; st.digits[11] = "0";
            }
        } else {
            st.mode = "date";
            st.template = templates.date;
            st.digits = st.digits.slice(0, 8);
        }
        st.cursor = Math.min(st.cursor, st.template.slots.length - 1);
        commit(st);
    }

    // Month/year navigation in the panel head: with a complete date already
    // selected, moving the shown month or year moves the DATE with it (day
    // clamped to the target month's length) and commits immediately - no
    // extra day click needed. Without a date it just navigates the view.
    function navigateShown(st, year, month) {
        st.shownYear = year;
        st.shownMonth = month;
        var parts = selectedParts(st);
        if (parts.year !== null && parts.month !== null && parts.day !== null) {
            var maxDay = new Date(Date.UTC(year, month, 0)).getUTCDate();
            setDatePart(st, year, month, Math.min(parts.day, maxDay));
        } else {
            renderPanel(st);
        }
    }

    function closePanel() {
        if (panel) { panel.remove(); panel = null; panelInput = null; }
        if (panelToggle) {
            panelToggle.classList.remove("omp-datetime__toggle--open");
            panelToggle.setAttribute("aria-label", texts.open);
            panelToggle = null;
        }
    }

    function renderPanel(st) {
        if (!panel) { return; }
        panel.textContent = "";
        var parts = selectedParts(st);
        var now = new Date();
        var shownYear = st.shownYear;
        var shownMonth = st.shownMonth;

        if (st.mode !== "time") {
            var cal = document.createElement("div");
            cal.className = "omp-datetime-panel__calendar";

            var head = document.createElement("div");
            head.className = "omp-datetime-panel__head";
            var prev = document.createElement("button");
            prev.type = "button"; prev.className = "omp-datetime-panel__step"; prev.textContent = "‹";
            prev.addEventListener("click", function () {
                var month = st.shownMonth - 1;
                var year = st.shownYear;
                if (month < 1) { month = 12; year -= 1; }
                navigateShown(st, year, month);
            });
            var monthSelect = document.createElement("select");
            monthSelect.className = "omp-datetime-panel__select";
            monthNames().forEach(function (name, index) {
                var option = document.createElement("option");
                option.value = String(index + 1); option.textContent = name; option.selected = index + 1 === shownMonth;
                monthSelect.appendChild(option);
            });
            monthSelect.addEventListener("change", function () { navigateShown(st, st.shownYear, +monthSelect.value); });
            var yearSelect = document.createElement("select");
            yearSelect.className = "omp-datetime-panel__select";
            for (var y = shownYear - 12; y <= shownYear + 12; y++) {
                var yearOption = document.createElement("option");
                yearOption.value = String(y); yearOption.textContent = String(y); yearOption.selected = y === shownYear;
                yearSelect.appendChild(yearOption);
            }
            yearSelect.addEventListener("change", function () { navigateShown(st, +yearSelect.value, st.shownMonth); });
            var next = document.createElement("button");
            next.type = "button"; next.className = "omp-datetime-panel__step"; next.textContent = "›";
            next.addEventListener("click", function () {
                var month = st.shownMonth + 1;
                var year = st.shownYear;
                if (month > 12) { month = 1; year += 1; }
                navigateShown(st, year, month);
            });
            head.append(prev, monthSelect, yearSelect, next);
            cal.appendChild(head);

            var grid = document.createElement("div");
            grid.className = "omp-datetime-panel__grid";
            var weekHead = document.createElement("span");
            weekHead.className = "omp-datetime-panel__weekno";
            weekHead.textContent = texts.week;
            grid.appendChild(weekHead);
            weekdayNames().forEach(function (name) {
                var label = document.createElement("span");
                label.className = "omp-datetime-panel__weekday";
                label.textContent = name;
                grid.appendChild(label);
            });

            var first = new Date(Date.UTC(shownYear, shownMonth - 1, 1));
            var startOffset = (first.getUTCDay() + 6) % 7;
            var cursor = new Date(Date.UTC(shownYear, shownMonth - 1, 1 - startOffset));
            for (var week = 0; week < 6; week++) {
                var weekCell = document.createElement("span");
                weekCell.className = "omp-datetime-panel__weekno";
                weekCell.textContent = String(isoWeek(cursor));
                grid.appendChild(weekCell);
                for (var dayIndex = 0; dayIndex < 7; dayIndex++) {
                    (function (cellDate) {
                        var button = document.createElement("button");
                        button.type = "button";
                        button.className = "omp-datetime-panel__day";
                        button.textContent = String(cellDate.getUTCDate());
                        if (cellDate.getUTCMonth() !== shownMonth - 1) { button.classList.add("omp-datetime-panel__day--outside"); }
                        if (cellDate.getUTCFullYear() === now.getUTCFullYear() && cellDate.getUTCMonth() === now.getUTCMonth() && cellDate.getUTCDate() === now.getUTCDate()) {
                            button.classList.add("omp-datetime-panel__day--today");
                        }
                        if (parts.year === cellDate.getUTCFullYear() && parts.month === cellDate.getUTCMonth() + 1 && parts.day === cellDate.getUTCDate()) {
                            button.classList.add("omp-datetime-panel__day--selected");
                        }
                        button.addEventListener("click", function () {
                            st.shownYear = cellDate.getUTCFullYear();
                            st.shownMonth = cellDate.getUTCMonth() + 1;
                            setDatePart(st, cellDate.getUTCFullYear(), cellDate.getUTCMonth() + 1, cellDate.getUTCDate());
                        });
                        grid.appendChild(button);
                    })(new Date(cursor));
                    cursor.setUTCDate(cursor.getUTCDate() + 1);
                }
            }
            cal.appendChild(grid);
            panel.appendChild(cal);
        }

        if (st.mode !== "date") {
            var time = document.createElement("div");
            time.className = "omp-datetime-panel__time";
            var timeTitle = document.createElement("div");
            timeTitle.className = "omp-datetime-panel__time-title";
            timeTitle.textContent = texts.time;
            time.appendChild(timeTitle);

            var hours = document.createElement("div");
            hours.className = "omp-datetime-panel__hours";
            for (var hour = 0; hour < 24; hour++) {
                (function (value) {
                    var button = document.createElement("button");
                    button.type = "button";
                    button.className = "omp-datetime-panel__cell";
                    button.textContent = pad(value);
                    if (parts.hour === value) { button.classList.add("omp-datetime-panel__cell--selected"); }
                    button.addEventListener("click", function () { setTimePart(st, value, parts.minute === null ? 0 : null); });
                    hours.appendChild(button);
                })(hour);
            }
            time.appendChild(hours);

            var minutes = document.createElement("div");
            minutes.className = "omp-datetime-panel__minutes";
            var minuteValues = [];
            for (var m = 0; m < 60; m += 5) { minuteValues.push(m); }
            // The exact selected minute always appears, even off the 5-step
            // grid, so the picker never hides the current value.
            if (parts.minute !== null && minuteValues.indexOf(parts.minute) < 0) {
                minuteValues.push(parts.minute);
                minuteValues.sort(function (a, b) { return a - b; });
            }
            minuteValues.forEach(function (value) {
                var button = document.createElement("button");
                button.type = "button";
                button.className = "omp-datetime-panel__cell";
                button.textContent = ":" + pad(value);
                if (parts.minute === value) { button.classList.add("omp-datetime-panel__cell--selected"); }
                button.addEventListener("click", function () { setTimePart(st, parts.hour === null ? 0 : null, value); });
                minutes.appendChild(button);
            });
            time.appendChild(minutes);
            panel.appendChild(time);
        }

        var footer = document.createElement("div");
        footer.className = "omp-datetime-panel__footer";
        if (st.declaredMode === "datetime") {
            var timeChip = document.createElement("button");
            timeChip.type = "button";
            timeChip.className = "omp-datetime-panel__action";
            if (st.mode === "datetime") { timeChip.classList.add("omp-datetime-panel__action--on"); }
            timeChip.setAttribute("aria-pressed", st.mode === "datetime" ? "true" : "false");
            timeChip.textContent = texts.time;
            timeChip.addEventListener("click", function () { setTimeVisible(st, st.mode !== "datetime"); });
            footer.appendChild(timeChip);
        }
        var clear = document.createElement("button");
        clear.type = "button"; clear.className = "omp-datetime-panel__action"; clear.textContent = texts.clear;
        clear.addEventListener("click", function () {
            st.digits = st.template.slots.map(function () { return ""; });
            st.cursor = 0;
            st.hidden.value = "";
            st.hidden.dispatchEvent(new Event("change", { bubbles: true }));
            render(st);
            renderPanel(st);
        });
        var nowButton = document.createElement("button");
        nowButton.type = "button"; nowButton.className = "omp-datetime-panel__action"; nowButton.textContent = st.mode === "date" ? texts.today : texts.now;
        nowButton.addEventListener("click", function () {
            var current = new Date();
            st.shownYear = current.getUTCFullYear();
            st.shownMonth = current.getUTCMonth() + 1;
            if (st.mode === "time") {
                setTimePart(st, current.getUTCHours(), current.getUTCMinutes());
            } else {
                setDatePart(st, current.getUTCFullYear(), current.getUTCMonth() + 1, current.getUTCDate());
                if (st.mode === "datetime") { setTimePart(st, current.getUTCHours(), current.getUTCMinutes()); }
            }
        });
        // Reset restores the value from when the panel was opened, so a
        // series of exploratory clicks can be undone in one step.
        var reset = document.createElement("button");
        reset.type = "button"; reset.className = "omp-datetime-panel__action"; reset.textContent = texts.reset;
        // Nothing to undo until the value differs from when the panel opened.
        reset.disabled = st.hidden.value === st.openSnapshot;
        reset.addEventListener("click", function () {
            if (st.declaredMode === "datetime") {
                var stamp = st.openSnapshot || "";
                setTimeVisible(st, stamp.indexOf("T") >= 0 && !/T00:00$/.test(stamp));
            }
            if (st.hidden.value !== st.openSnapshot) {
                st.hidden.value = st.openSnapshot;
                st.hidden.dispatchEvent(new Event("change", { bubbles: true }));
            }
            digitsFromCanonical(st, st.openSnapshot);
            st.cursor = 0;
            render(st);
            renderPanel(st);
        });
        footer.append(clear, reset, nowButton);
        panel.appendChild(footer);

        // The caret sits under the field's toggle icon so the panel reads
        // as a speech bubble anchored to it. In wide fields the icon can
        // sit past the panel's width, so slide the panel toward the icon
        // first (never past the wrapper's edges) and aim the caret at the
        // icon from wherever the panel ended up. Runs on every render
        // because toggling the time block changes the panel's width.
        var toggleCenter = st.toggle.offsetLeft + st.toggle.offsetWidth / 2;
        var maxLeft = Math.max(0, st.wrapper.offsetWidth - panel.offsetWidth);
        var left = Math.min(Math.max(toggleCenter + 20 - panel.offsetWidth, 0), maxLeft);
        panel.style.left = left + "px";
        panel.style.setProperty("--omp-caret-left", Math.max(toggleCenter - left, 16) + "px");
    }

    function openPanel(st) {
        closePanel();
        var parts = selectedParts(st);
        var now = new Date();
        st.shownYear = parts.year || now.getUTCFullYear();
        st.shownMonth = parts.month || now.getUTCMonth() + 1;
        st.openSnapshot = st.hidden.value;
        panel = document.createElement("div");
        panel.className = "omp-datetime-panel";
        panelInput = st.input;
        panelToggle = st.toggle;
        st.toggle.classList.add("omp-datetime__toggle--open");
        st.toggle.setAttribute("aria-label", texts.close);
        st.wrapper.appendChild(panel);
        renderPanel(st);
    }

    function enhance(input) {
        if (input._ompDatetime) { return; }
        var mode = (input.getAttribute("data-omp-datetime") || "datetime").toLowerCase();
        var template = templates[mode] || templates.datetime;

        var hidden = document.createElement("input");
        hidden.type = "hidden";
        hidden.name = input.name;
        hidden.value = input.value || "";
        input.removeAttribute("name");

        var wrapper = document.createElement("span");
        wrapper.className = input.getAttribute("data-omp-datetime-no-toggle") === null
            ? "omp-datetime"
            : "omp-datetime omp-datetime--no-toggle";
        input.parentNode.insertBefore(wrapper, input);
        wrapper.appendChild(input);
        wrapper.appendChild(hidden);

        if (input.getAttribute("data-omp-datetime-utc") === "true") {
            var suffix = document.createElement("span");
            suffix.className = "omp-datetime__suffix";
            suffix.textContent = "UTC";
            suffix.setAttribute("aria-hidden", "true");
            wrapper.appendChild(suffix);
        }

        // Quick clear between the UTC suffix and the calendar icon; hidden
        // while the field is empty or disabled.
        var clearBtn = document.createElement("button");
        clearBtn.type = "button";
        clearBtn.className = "omp-datetime__clear";
        clearBtn.setAttribute("aria-label", texts.clear);
        wrapper.appendChild(clearBtn);

        // The range popup embeds fields next to a shared calendar; those
        // fields keep the mask and quick-clear but skip their own calendar
        // toggle - one calendar, not three.
        var wantsToggle = input.getAttribute("data-omp-datetime-no-toggle") === null;
        var toggle = null;
        if (wantsToggle) {
            toggle = document.createElement("button");
            toggle.type = "button";
            toggle.className = "omp-datetime__toggle";
            toggle.setAttribute("aria-label", texts.open);
            wrapper.appendChild(toggle);
        }

        var st = {
            input: input, hidden: hidden, wrapper: wrapper, toggle: toggle, clear: clearBtn,
            mode: mode, declaredMode: mode, template: template,
            digits: [], cursor: 0, shownYear: 0, shownMonth: 1, openSnapshot: ""
        };
        // Datetime fields start date-only (time off, internally midnight);
        // a stored value with an actual time keeps the time visible so the
        // field never hides part of what it holds.
        if (mode === "datetime" && !(hidden.value.indexOf("T") >= 0 && !/T00:00$/.test(hidden.value))) {
            st.mode = "date";
            st.template = templates.date;
        }
        input._ompDatetime = st;
        input.type = "text";
        input.autocomplete = "off";
        input.spellcheck = false;
        input.classList.add("omp-datetime__input");
        digitsFromCanonical(st, hidden.value);
        render(st);

        input.addEventListener("keydown", onKeydown);
        input.addEventListener("focus", function () { st.cursor = 0; highlight(st); });
        input.addEventListener("blur", function () { window.setTimeout(function () { onBlurCommit(st); }, 0); });
        input.addEventListener("mouseup", function (event) {
            // A click anywhere in a part selects that whole segment.
            event.preventDefault();
            var position = input.selectionStart || 0;
            var best = 0;
            st.template.slots.forEach(function (slotPosition, index) {
                if (slotPosition <= position) { best = index; }
            });
            st.cursor = segmentAt(st, best)[0];
            highlight(st);
        });
        // The lock/cancel flow restores display strings; re-parse them into
        // the canonical hidden value.
        input.addEventListener("omp-datetime-restore", function () {
            var digits = input.value.replace(/[^0-9]/g, "");
            // A restored display string carries its own shape: 8 digits is a
            // date-only value, 12 is a full datetime.
            if (st.declaredMode === "datetime") {
                var wantTime = digits.length > 8;
                st.mode = wantTime ? "datetime" : "date";
                st.template = wantTime ? templates.datetime : templates.date;
                st.cursor = Math.min(st.cursor, st.template.slots.length - 1);
            }
            var complete = digits.length === st.template.slots.length;
            st.digits = st.template.slots.map(function (_, index) { return complete ? digits[index] : ""; });
            st.hidden.value = complete ? (canonicalFromDigits(st) || "") : "";
            render(st);
        });

        clearBtn.addEventListener("click", function () {
            if (input.disabled) { return; }
            st.digits = st.template.slots.map(function () { return ""; });
            st.cursor = 0;
            if (st.hidden.value !== "") {
                st.hidden.value = "";
                st.hidden.dispatchEvent(new Event("change", { bubbles: true }));
            }
            render(st);
            if (panelInput === input && panel) { renderPanel(st); }
            input.focus();
        });

        if (toggle) {
            toggle.addEventListener("click", function () {
                if (input.disabled) { return; }
                if (panelInput === input) { closePanel(); } else { openPanel(st); }
            });
        }
    }

    // --- range mode ---------------------------------------------------------
    // One field showing a whole period; the popup pairs a preset rail with
    // Från/Till date fields (full omp-datetime fields, calendars included).
    // Pages write:
    //   <span data-omp-daterange data-range-name="range" data-from-name="from"
    //         data-to-name="to" data-from-text="..." data-to-text="..."
    //         data-apply-text="...">
    //       <input type="hidden" name="range|from|to" value="..."> (all three)
    //       <span data-omp-daterange-preset="7d">Last 7 days</span> ...
    //   </span>
    // The hidden inputs are page-owned (server-rendered values, page resx
    // labels on the presets), so the page's GET contract and localization
    // stay where they were. Picking a preset applies immediately; custom
    // dates apply through the footer button. Either date may stay empty for
    // an open-ended period; both empty applies as the first preset.

    var rangePanel = null;
    var rangeContainer = null;

    function closeRangePanel() {
        if (rangePanel) {
            // A child calendar panel lives inside the range popup; drop it
            // with the popup or the module would point at a detached panel.
            if (panel && rangePanel.contains(panel)) { closePanel(); }
            rangePanel.remove();
            rangePanel = null;
            rangeContainer = null;
        }
    }

    function enhanceRange(container) {
        if (container._ompDaterange) { return; }

        var names = {
            range: container.getAttribute("data-range-name") || "range",
            from: container.getAttribute("data-from-name") || "from",
            to: container.getAttribute("data-to-name") || "to"
        };
        var hiddenOf = function (name) {
            return container.querySelector('input[type="hidden"][name="' + name + '"]');
        };
        var hiddens = { range: hiddenOf(names.range), from: hiddenOf(names.from), to: hiddenOf(names.to) };
        if (!hiddens.range || !hiddens.from || !hiddens.to) { return; }

        var presets = Array.prototype.map.call(
            container.querySelectorAll("[data-omp-daterange-preset]"),
            function (el) {
                el.hidden = true;
                return { key: el.getAttribute("data-omp-daterange-preset"), label: el.textContent };
            });

        // data-max="today" caps the pickable range at the current UTC day:
        // future days, months and years are disabled in the calendar, and
        // typed values clamp on apply. Opt-in - other consumers may
        // legitimately point into the future.
        function maxIso() {
            if (container.getAttribute("data-max") !== "today") { return null; }
            var now = new Date();
            return now.getUTCFullYear() + "-" + pad(now.getUTCMonth() + 1) + "-" + pad(now.getUTCDate());
        }

        function currentLabel() {
            var match = presets.filter(function (preset) { return preset.key === hiddens.range.value; })[0];
            if (match) { return match.label; }
            var from = hiddens.from.value;
            var to = hiddens.to.value;
            if (from || to) { return (from || "") + " – " + (to || ""); }
            return presets.length > 0 ? presets[0].label : "";
        }

        var field = document.createElement("button");
        field.type = "button";
        // Pages hand the button their host input class (e.g. ip-field-input)
        // so it dresses like the neighboring form fields.
        field.className = ("omp-daterange__field " + (container.getAttribute("data-field-class") || "")).trim();
        field.textContent = currentLabel();
        container.appendChild(field);
        container.classList.add("omp-daterange");

        var st = { container: container, hiddens: hiddens, presets: presets, field: field };
        container._ompDaterange = st;

        function applyAndClose(rangeValue, fromValue, toValue) {
            var changed = hiddens.range.value !== rangeValue
                || hiddens.from.value !== fromValue
                || hiddens.to.value !== toValue;
            hiddens.range.value = rangeValue;
            hiddens.from.value = fromValue;
            hiddens.to.value = toValue;
            field.textContent = currentLabel();
            closeRangePanel();
            if (changed) {
                hiddens.range.dispatchEvent(new Event("change", { bubbles: true }));
                container.dispatchEvent(new CustomEvent("omp-daterange-change", { bubbles: true }));
            }
        }

        function openRangePanel() {
            closeRangePanel();
            closePanel();
            rangePanel = document.createElement("div");
            rangePanel.className = "omp-datetime-panel omp-daterange-panel";
            rangeContainer = container;

            var rail = document.createElement("div");
            rail.className = "omp-daterange-panel__presets";
            presets.forEach(function (preset) {
                var button = document.createElement("button");
                button.type = "button";
                button.className = "omp-daterange-panel__preset";
                if (preset.key === hiddens.range.value) { button.classList.add("omp-daterange-panel__preset--active"); }
                button.textContent = preset.label;
                button.addEventListener("click", function () { applyAndClose(preset.key, "", ""); });
                rail.appendChild(button);
            });
            rangePanel.appendChild(rail);

            var fields = document.createElement("div");
            fields.className = "omp-daterange-panel__fields";
            var rows = document.createElement("div");
            rows.className = "omp-daterange-panel__rows";
            fields.appendChild(rows);
            var makeRow = function (labelText, value) {
                var row = document.createElement("label");
                row.className = "omp-daterange-panel__row";
                var caption = document.createElement("span");
                caption.textContent = labelText;
                row.appendChild(caption);
                var input = document.createElement("input");
                input.setAttribute("data-omp-datetime", "date");
                // The shared range calendar below serves both fields; the
                // fields keep the mask and quick-clear only.
                input.setAttribute("data-omp-datetime-no-toggle", "");
                input.value = value;
                row.appendChild(input);
                rows.appendChild(row);
                enhance(input);
                return input;
            };
            var fromInput = makeRow(container.getAttribute("data-from-text") || names.from, hiddens.from.value);
            var toInput = makeRow(container.getAttribute("data-to-text") || names.to, hiddens.to.value);

            // --- shared range calendar: click start, click end ------------
            var calHost = document.createElement("div");
            calHost.className = "omp-datetime-panel__calendar";
            fields.appendChild(calHost);

            var initialIso = hiddens.from.value || hiddens.to.value;
            var initial = /^\d{4}-\d{2}-\d{2}/.test(initialIso || "")
                ? { year: +initialIso.slice(0, 4), month: +initialIso.slice(5, 7) }
                : { year: new Date().getUTCFullYear(), month: new Date().getUTCMonth() + 1 };
            var cal = { year: initial.year, month: initial.month, pendingStart: null, hoverIso: null };

            function isoOfCell(cellDate) {
                return cellDate.getUTCFullYear() + "-" + pad(cellDate.getUTCMonth() + 1) + "-" + pad(cellDate.getUTCDate());
            }

            function setFieldDate(input, iso) {
                var fieldState = state(input);
                digitsFromCanonical(fieldState, iso);
                fieldState.hidden.value = iso;
                fieldState.cursor = 0;
                render(fieldState);
            }

            function onDayPicked(iso) {
                if (cal.pendingStart === null) {
                    cal.pendingStart = iso;
                    cal.hoverIso = null;
                    setFieldDate(fromInput, iso);
                    setFieldDate(toInput, "");
                } else {
                    var start = cal.pendingStart;
                    var end = iso;
                    if (end < start) { var swap = start; start = end; end = swap; }
                    setFieldDate(fromInput, start);
                    setFieldDate(toInput, end);
                    cal.pendingStart = null;
                    cal.hoverIso = null;
                }
                renderRangeCalendar();
            }

            function renderRangeCalendar() {
                calHost.textContent = "";
                var from = state(fromInput).hidden.value;
                var to = state(toInput).hidden.value;
                // While the second click is pending, the preview range runs
                // from the first click to the hovered day.
                var previewFrom = from;
                var previewTo = to;
                if (cal.pendingStart !== null && cal.hoverIso !== null) {
                    previewFrom = cal.pendingStart < cal.hoverIso ? cal.pendingStart : cal.hoverIso;
                    previewTo = cal.pendingStart < cal.hoverIso ? cal.hoverIso : cal.pendingStart;
                }
                var now = new Date();
                var max = maxIso();
                var maxYear = max ? +max.slice(0, 4) : 0;
                var maxMonth = max ? +max.slice(5, 7) : 0;

                var head = document.createElement("div");
                head.className = "omp-datetime-panel__head";
                var prev = document.createElement("button");
                prev.type = "button"; prev.className = "omp-datetime-panel__step"; prev.textContent = "‹";
                prev.addEventListener("click", function () {
                    cal.month -= 1;
                    if (cal.month < 1) { cal.month = 12; cal.year -= 1; }
                    renderRangeCalendar();
                });
                var monthSelect = document.createElement("select");
                monthSelect.className = "omp-datetime-panel__select";
                monthNames().forEach(function (name, index) {
                    var option = document.createElement("option");
                    option.value = String(index + 1); option.textContent = name; option.selected = index + 1 === cal.month;
                    if (max && cal.year === maxYear && index + 1 > maxMonth) { option.disabled = true; }
                    monthSelect.appendChild(option);
                });
                monthSelect.addEventListener("change", function () { cal.month = +monthSelect.value; renderRangeCalendar(); });
                var yearSelect = document.createElement("select");
                yearSelect.className = "omp-datetime-panel__select";
                var lastYear = max ? Math.min(cal.year + 12, maxYear) : cal.year + 12;
                for (var y = cal.year - 12; y <= lastYear; y++) {
                    var yearOption = document.createElement("option");
                    yearOption.value = String(y); yearOption.textContent = String(y); yearOption.selected = y === cal.year;
                    yearSelect.appendChild(yearOption);
                }
                yearSelect.addEventListener("change", function () {
                    cal.year = +yearSelect.value;
                    if (max && cal.year === maxYear && cal.month > maxMonth) { cal.month = maxMonth; }
                    renderRangeCalendar();
                });
                var next = document.createElement("button");
                next.type = "button"; next.className = "omp-datetime-panel__step"; next.textContent = "›";
                next.disabled = !!max && cal.year === maxYear && cal.month === maxMonth;
                next.addEventListener("click", function () {
                    cal.month += 1;
                    if (cal.month > 12) { cal.month = 1; cal.year += 1; }
                    renderRangeCalendar();
                });
                head.append(prev, monthSelect, yearSelect, next);
                calHost.appendChild(head);

                var grid = document.createElement("div");
                grid.className = "omp-datetime-panel__grid";
                var weekHead = document.createElement("span");
                weekHead.className = "omp-datetime-panel__weekno";
                weekHead.textContent = texts.week;
                grid.appendChild(weekHead);
                weekdayNames().forEach(function (name) {
                    var label = document.createElement("span");
                    label.className = "omp-datetime-panel__weekday";
                    label.textContent = name;
                    grid.appendChild(label);
                });

                var first = new Date(Date.UTC(cal.year, cal.month - 1, 1));
                var startOffset = (first.getUTCDay() + 6) % 7;
                var cursor = new Date(Date.UTC(cal.year, cal.month - 1, 1 - startOffset));
                for (var week = 0; week < 6; week++) {
                    var weekCell = document.createElement("span");
                    weekCell.className = "omp-datetime-panel__weekno";
                    weekCell.textContent = String(isoWeek(cursor));
                    grid.appendChild(weekCell);
                    for (var dayIndex = 0; dayIndex < 7; dayIndex++) {
                        (function (cellDate) {
                            var iso = isoOfCell(cellDate);
                            var button = document.createElement("button");
                            button.type = "button";
                            button.className = "omp-datetime-panel__day";
                            button.textContent = String(cellDate.getUTCDate());
                            if (cellDate.getUTCMonth() !== cal.month - 1) { button.classList.add("omp-datetime-panel__day--outside"); }
                            if (cellDate.getUTCFullYear() === now.getUTCFullYear() && cellDate.getUTCMonth() === now.getUTCMonth() && cellDate.getUTCDate() === now.getUTCDate()) {
                                button.classList.add("omp-datetime-panel__day--today");
                            }
                            if (iso === from || iso === to || iso === cal.pendingStart) {
                                button.classList.add("omp-datetime-panel__day--selected");
                            } else if (previewFrom && previewTo && iso > previewFrom && iso < previewTo) {
                                button.classList.add("omp-datetime-panel__day--range");
                            }
                            if (max && iso > max) {
                                button.disabled = true;
                                button.classList.add("omp-datetime-panel__day--disabled");
                            } else {
                                button.addEventListener("click", function () { onDayPicked(iso); });
                                if (cal.pendingStart !== null) {
                                    button.addEventListener("mouseenter", function () {
                                        if (cal.hoverIso !== iso) { cal.hoverIso = iso; renderRangeCalendar(); }
                                    });
                                }
                            }
                            grid.appendChild(button);
                        })(new Date(cursor));
                        cursor.setUTCDate(cursor.getUTCDate() + 1);
                    }
                }
                calHost.appendChild(grid);
            }

            // Typed edits move the highlights too; the hidden inputs bubble
            // a change on every committed mask edit or quick-clear.
            rows.addEventListener("change", function () {
                cal.pendingStart = null;
                cal.hoverIso = null;
                renderRangeCalendar();
            });

            renderRangeCalendar();

            var footer = document.createElement("div");
            footer.className = "omp-datetime-panel__footer";
            // Bottom-left: jump the calendar view back to the current month,
            // and clear both dates for a fresh pick (each field also has its
            // own quick-clear X for emptying just one end).
            var todayButton = document.createElement("button");
            todayButton.type = "button";
            todayButton.className = "omp-datetime-panel__action";
            todayButton.textContent = texts.today;
            todayButton.addEventListener("click", function () {
                var current = new Date();
                cal.year = current.getUTCFullYear();
                cal.month = current.getUTCMonth() + 1;
                renderRangeCalendar();
            });
            var clearButton = document.createElement("button");
            clearButton.type = "button";
            clearButton.className = "omp-datetime-panel__action";
            clearButton.textContent = texts.clear;
            clearButton.addEventListener("click", function () {
                setFieldDate(fromInput, "");
                setFieldDate(toInput, "");
                cal.pendingStart = null;
                cal.hoverIso = null;
                renderRangeCalendar();
            });
            footer.append(todayButton, clearButton);
            var apply = document.createElement("button");
            apply.type = "button";
            apply.className = "omp-datetime-panel__action omp-datetime-panel__action--on omp-daterange-panel__apply";
            apply.textContent = container.getAttribute("data-apply-text") || "OK";
            apply.addEventListener("click", function () {
                var fromValue = state(fromInput).hidden.value;
                var toValue = state(toInput).hidden.value;
                if (fromValue === "" && toValue === "" && presets.length > 0) {
                    applyAndClose(presets[0].key, "", "");
                } else {
                    // Reversed bounds are swapped and typed future dates
                    // clamp to the cap, rather than rejected - the server
                    // does the same, so the page never disagrees.
                    var cap = maxIso();
                    if (cap) {
                        if (fromValue > cap) { fromValue = cap; }
                        if (toValue > cap) { toValue = cap; }
                    }
                    if (fromValue !== "" && toValue !== "" && fromValue > toValue) {
                        var swap = fromValue; fromValue = toValue; toValue = swap;
                    }
                    applyAndClose("custom", fromValue, toValue);
                }
            });
            footer.appendChild(apply);
            fields.appendChild(footer);
            rangePanel.appendChild(fields);

            container.appendChild(rangePanel);
        }

        field.addEventListener("click", function () {
            if (rangeContainer === container) { closeRangePanel(); } else { openRangePanel(); }
        });
    }

    function init(root) {
        (root || document).querySelectorAll("input[data-omp-datetime]").forEach(enhance);
        (root || document).querySelectorAll("[data-omp-daterange]").forEach(enhanceRange);
    }

    document.addEventListener("click", function (event) {
        // A click on a panel button re-renders the panel synchronously, so
        // by the time this bubbles the target is detached; a detached
        // target was inside the panel and must not close it.
        if (!event.target.isConnected) { return; }
        if (panel && !panel.contains(event.target) && !(panelInput && panelInput._ompDatetime.wrapper.contains(event.target))) {
            closePanel();
        }
        if (rangePanel && rangeContainer && !rangeContainer.contains(event.target)) {
            closeRangePanel();
        }
    });
    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            // The child calendar closes first; a second Escape closes the
            // range popup itself.
            if (panel) { closePanel(); } else { closeRangePanel(); }
        }
    });

    window.ompDatetime = { init: init };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { init(document); });
    } else {
        init(document);
    }
}());
