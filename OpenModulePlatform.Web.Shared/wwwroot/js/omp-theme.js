// OMP theme contract, version 1. See docs/THEME_CONTRACT.md.
//
// Load this file synchronously (no defer/async) in <head>, before the first
// stylesheet that consumes the --omp-* tokens:
//
//   <script src="~/_content/OpenModulePlatform.Web.Shared/js/omp-theme.js"></script>
//
// It is an external file on purpose: the Portal and Auth Content-Security-Policy
// forbid inline scripts, and a synchronous head script is the only way to set the
// theme before the first paint without them. It only touches
// document.documentElement while the head is parsing; the menu wiring is
// event delegation on document, so it works for Razor pages, Blazor re-renders
// and markup added later without any init call.
//
// Contract on <html>:
//   data-theme       the palette in use:        light | dark
//   data-theme-mode  the user's choice:         system | light | dark
//
// Storage: OMP_THEME_PREFERENCE = {"version":1,"mode":"...","revision":"..."}
// in a host-only cookie (Path=/, SameSite=Lax, Secure on HTTPS) mirrored to
// localStorage. Denied storage keeps working for the current page session.
// This file never reads or writes any other application's preferences.
(function () {
    'use strict';

    var PREFERENCE_NAME = 'OMP_THEME_PREFERENCE';
    var PREFERENCE_VERSION = 1;
    var COOKIE_MAX_AGE_SECONDS = 365 * 24 * 60 * 60;
    var CHANGE_EVENT = 'omp:theme-changed';
    var MODES = ['system', 'light', 'dark'];
    var root = document.documentElement;
    var script = document.currentScript;
    var cookieDomain = resolveCookieDomain(script ? script.getAttribute('data-cookie-domain') : '');
    var darkQuery = typeof window.matchMedia === 'function'
        ? window.matchMedia('(prefers-color-scheme: dark)')
        : null;
    // Holds the choice when both cookie and localStorage are denied, so the
    // switch still works until the page is left.
    var sessionPreference = null;

    function normalizeMode(value) {
        return MODES.indexOf(value) >= 0 ? value : null;
    }

    function parsePreference(raw) {
        if (!raw) {
            return null;
        }

        try {
            var value = JSON.parse(raw);
            if (!value || value.version !== PREFERENCE_VERSION || !normalizeMode(value.mode)) {
                return null;
            }

            return {
                version: PREFERENCE_VERSION,
                mode: value.mode,
                revision: typeof value.revision === 'string' ? value.revision : ''
            };
        } catch (error) {
            return null;
        }
    }

    // A shared parent domain is opt-in (data-cookie-domain on the script tag) and
    // only honoured when the current host actually lies under it.
    function resolveCookieDomain(value) {
        var domain = (value || '').trim().replace(/^\./, '').toLowerCase();
        if (!domain) {
            return '';
        }

        var host = (window.location.hostname || '').toLowerCase();
        return host === domain || host.slice(-(domain.length + 1)) === '.' + domain ? domain : '';
    }

    function readCookie() {
        try {
            var parts = document.cookie ? document.cookie.split(';') : [];
            for (var i = 0; i < parts.length; i++) {
                var part = parts[i].trim();
                if (part.indexOf(PREFERENCE_NAME + '=') === 0) {
                    return parsePreference(decodeURIComponent(part.slice(PREFERENCE_NAME.length + 1)));
                }
            }
        } catch (error) {
            // Cookie access denied or a malformed value: treat as absent.
        }

        return null;
    }

    function writeCookie(preference) {
        try {
            var cookie = PREFERENCE_NAME + '=' + encodeURIComponent(JSON.stringify(preference))
                + '; Path=/; Max-Age=' + COOKIE_MAX_AGE_SECONDS + '; SameSite=Lax';
            if (cookieDomain) {
                cookie += '; Domain=' + cookieDomain;
            }

            if (window.location.protocol === 'https:') {
                cookie += '; Secure';
            }

            document.cookie = cookie;
        } catch (error) {
            // Denied: the localStorage mirror or the session value takes over.
        }
    }

    function readLocal() {
        try {
            return parsePreference(window.localStorage.getItem(PREFERENCE_NAME));
        } catch (error) {
            return null;
        }
    }

    function writeLocal(preference) {
        try {
            window.localStorage.setItem(PREFERENCE_NAME, JSON.stringify(preference));
        } catch (error) {
            // Denied or full: the cookie or the session value takes over.
        }
    }

    // A revision starts with its creation time (newRevision). Anything else,
    // for example a hand-written value, counts as oldest.
    function revisionTime(preference) {
        var time = parseInt(preference.revision.split('-')[0], 36);
        return isFinite(time) ? time : 0;
    }

    // The newest of the given preferences by revision. On a tie, or when a
    // revision cannot be read, the earlier argument wins: the cookie is the shared
    // value (it reaches every port and app on the host), so it beats the
    // localStorage mirror unless the mirror is provably newer, as it is when a
    // cookie write was silently dropped.
    function newest() {
        var best = null;
        for (var i = 0; i < arguments.length; i++) {
            var candidate = arguments[i];
            if (candidate && (!best || revisionTime(candidate) > revisionTime(best))) {
                best = candidate;
            }
        }

        return best;
    }

    function readPreference() {
        return newest(readCookie(), readLocal(), sessionPreference);
    }

    function systemTheme() {
        return darkQuery && darkQuery.matches ? 'dark' : 'light';
    }

    function currentMode() {
        return normalizeMode(root.getAttribute('data-theme-mode')) || 'system';
    }

    function apply(mode, source) {
        var resolvedMode = normalizeMode(mode) || 'system';
        var theme = resolvedMode === 'system' ? systemTheme() : resolvedMode;
        var changed = root.getAttribute('data-theme') !== theme
            || root.getAttribute('data-theme-mode') !== resolvedMode;
        root.setAttribute('data-theme', theme);
        root.setAttribute('data-theme-mode', resolvedMode);
        if (changed && source) {
            dispatchChange(resolvedMode, theme, source);
        }
    }

    function dispatchChange(mode, theme, source) {
        var detail = { mode: mode, theme: theme, source: source };
        var event;
        if (typeof window.CustomEvent === 'function') {
            event = new window.CustomEvent(CHANGE_EVENT, { detail: detail });
        } else {
            event = document.createEvent('CustomEvent');
            event.initCustomEvent(CHANGE_EVENT, false, false, detail);
        }

        window.dispatchEvent(event);
    }

    function newRevision() {
        return Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10);
    }

    function setMode(mode) {
        var resolvedMode = normalizeMode(mode);
        if (!resolvedMode) {
            return;
        }

        var preference = { version: PREFERENCE_VERSION, mode: resolvedMode, revision: newRevision() };
        sessionPreference = preference;
        writeCookie(preference);
        writeLocal(preference);
        apply(resolvedMode, 'user');
    }

    // Reads both stores and the session value, copies the newest to any store
    // that holds something else, and returns it (null when nothing is stored).
    function syncStores() {
        var cookie = readCookie();
        var local = readLocal();
        var preference = newest(cookie, local, sessionPreference);
        if (preference && (!local || local.revision !== preference.revision)) {
            writeLocal(preference);
        }

        if (preference && (!cookie || cookie.revision !== preference.revision)) {
            writeCookie(preference);
        }

        return preference;
    }

    // Re-read storage when another tab, port or app on the host may have changed it.
    function refreshFromStorage(source) {
        var preference = syncStores();
        if (preference) {
            apply(preference.mode, source);
        }
    }

    function initialize() {
        var preference = syncStores();
        apply(preference ? preference.mode : 'system', null);
    }

    // ---- Menu -------------------------------------------------------------
    //
    // Markup (rendered by the OmpThemeSwitch view component and the Blazor top bar):
    //   [data-omp-theme-switch]
    //     button[data-omp-theme-toggle][aria-expanded]
    //     [data-omp-theme-menu][role=menu][hidden]
    //       button[role=menuitemradio][data-omp-theme-option=system|light|dark]

    function getOptions(menu) {
        return Array.prototype.slice.call(menu.querySelectorAll('[data-omp-theme-option]'));
    }

    function syncMenu(menu) {
        var mode = currentMode();
        getOptions(menu).forEach(function (option) {
            var checked = option.getAttribute('data-omp-theme-option') === mode;
            option.setAttribute('aria-checked', checked ? 'true' : 'false');
            option.tabIndex = -1;
        });
    }

    function openMenu(container, focusTarget) {
        var toggle = container.querySelector('[data-omp-theme-toggle]');
        var menu = container.querySelector('[data-omp-theme-menu]');
        if (!toggle || !menu) {
            return;
        }

        closeAll(container);
        syncMenu(menu);
        menu.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
        var options = getOptions(menu);
        var target = focusTarget === 'last'
            ? options[options.length - 1]
            : menu.querySelector('[aria-checked="true"]') || options[0];
        if (target) {
            target.focus();
        }
    }

    function closeMenu(container, returnFocus) {
        var toggle = container.querySelector('[data-omp-theme-toggle]');
        var menu = container.querySelector('[data-omp-theme-menu]');
        if (!menu || menu.hidden) {
            return;
        }

        menu.hidden = true;
        if (toggle) {
            toggle.setAttribute('aria-expanded', 'false');
            if (returnFocus) {
                toggle.focus();
            }
        }
    }

    function closeAll(except) {
        var containers = document.querySelectorAll('[data-omp-theme-switch]');
        for (var i = 0; i < containers.length; i++) {
            if (containers[i] !== except) {
                closeMenu(containers[i], false);
            }
        }
    }

    function moveFocus(menu, step) {
        var options = getOptions(menu);
        if (options.length === 0) {
            return;
        }

        var index = options.indexOf(document.activeElement);
        var next = index < 0 ? 0 : (index + step + options.length) % options.length;
        options[next].focus();
    }

    function handleClick(event) {
        var target = event.target && event.target.closest ? event.target : null;
        var container = target ? target.closest('[data-omp-theme-switch]') : null;
        if (!container) {
            closeAll(null);
            return;
        }

        var option = target.closest('[data-omp-theme-option]');
        if (option) {
            event.preventDefault();
            setMode(option.getAttribute('data-omp-theme-option'));
            closeMenu(container, true);
            return;
        }

        if (target.closest('[data-omp-theme-toggle]')) {
            event.preventDefault();
            var menu = container.querySelector('[data-omp-theme-menu]');
            if (menu && menu.hidden) {
                openMenu(container, null);
            } else {
                closeMenu(container, true);
            }
        }
    }

    function handleKeydown(event) {
        var target = event.target && event.target.closest ? event.target : null;
        var container = target ? target.closest('[data-omp-theme-switch]') : null;
        if (!container) {
            if (event.key === 'Escape') {
                closeAll(null);
            }

            return;
        }

        var menu = container.querySelector('[data-omp-theme-menu]');
        var inToggle = !!target.closest('[data-omp-theme-toggle]');
        if (inToggle && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
            event.preventDefault();
            openMenu(container, event.key === 'ArrowUp' ? 'last' : null);
            return;
        }

        if (!menu || menu.hidden) {
            return;
        }

        switch (event.key) {
            case 'Escape':
                event.preventDefault();
                // Keep the top bar's own Escape handler from acting on the same press.
                event.stopPropagation();
                closeMenu(container, true);
                break;
            case 'ArrowDown':
                event.preventDefault();
                moveFocus(menu, 1);
                break;
            case 'ArrowUp':
                event.preventDefault();
                moveFocus(menu, -1);
                break;
            case 'Home':
                event.preventDefault();
                getOptions(menu)[0].focus();
                break;
            case 'End':
                event.preventDefault();
                getOptions(menu).slice(-1)[0].focus();
                break;
            case 'Tab':
                closeMenu(container, false);
                break;
            default:
                break;
        }
    }

    initialize();

    if (darkQuery) {
        var onSystemChange = function () {
            if (currentMode() === 'system') {
                apply('system', 'system');
            }
        };
        if (typeof darkQuery.addEventListener === 'function') {
            darkQuery.addEventListener('change', onSystemChange);
        } else if (typeof darkQuery.addListener === 'function') {
            darkQuery.addListener(onSystemChange);
        }
    }

    window.addEventListener('storage', function (event) {
        if (event.key === PREFERENCE_NAME) {
            refreshFromStorage('storage');
        }
    });
    window.addEventListener('pageshow', function () { refreshFromStorage('storage'); });
    window.addEventListener('focus', function () { refreshFromStorage('storage'); });
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') {
            refreshFromStorage('storage');
        }
    });
    // Blazor enhanced navigation (and any other DOM patcher) may rewrite <html>
    // from server markup that carries no theme attributes; put them back.
    if (typeof window.MutationObserver === 'function') {
        new window.MutationObserver(function () {
            if (!root.hasAttribute('data-theme') || !root.hasAttribute('data-theme-mode')) {
                var preference = readPreference();
                apply(preference ? preference.mode : 'system', null);
            }
        }).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-theme-mode'] });
    }

    document.addEventListener('click', handleClick);
    document.addEventListener('keydown', handleKeydown, true);

    window.ompTheme = {
        version: PREFERENCE_VERSION,
        changeEvent: CHANGE_EVENT,
        getMode: currentMode,
        getTheme: function () { return root.getAttribute('data-theme') || 'light'; },
        setMode: setMode
    };
})();
