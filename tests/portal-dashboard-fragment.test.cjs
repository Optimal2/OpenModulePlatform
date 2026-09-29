// Run with: node --test tests/portal-dashboard-fragment.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function dashboard(initial = 'timeout', responses = ['timeout', 'timeout']) {
    const timers = [];
    const requests = [];
    let current;
    let attached = true;
    let respond = async () => responses.shift();
    const element = {
        querySelector: () => current,
        getBoundingClientRect: () => ({ width: 416 })
    };
    function fragment(kind) {
        const classes = new Set([kind === 'timeout' ? 'is-loading' : kind === 'loaded' ? 'is-wide' : 'is-unavailable']);
        const placeholder = { textContent: kind };
        return {
            dataset: { moduleFragmentWidgetId: '7', ...(kind === 'timeout' ? { moduleFragmentRetry: 'timeout' } : {}) },
            classList: { add: value => classes.add(value), remove: value => classes.delete(value), contains: value => classes.has(value) },
            querySelector: () => placeholder,
            closest: () => element,
            replaceWith: value => { current = value; },
            placeholder
        };
    }
    current = fragment(initial);
    const root = {
        dataset: { moduleFragmentUrl: '/?handler=ModuleFragment', moduleFragmentUnavailableLabel: 'Unavailable' },
        contains: () => attached,
        querySelector: () => null,
        querySelectorAll: selector => selector === '[data-module-fragment-retry="timeout"]' && current.dataset.moduleFragmentRetry
            ? [current] : []
    };
    const context = {
        URL,
        window: {
            location: { href: 'http://localhost/' }, addEventListener() {},
            setTimeout: (callback, delay) => { timers.push({ callback, delay }); }
        },
        document: {
            readyState: 'loading', addEventListener() {},
            createElement: () => {
                let value;
                return {
                    set innerHTML(kind) { value = kind === 'invalid' ? null : fragment(kind); },
                    content: { querySelector: () => value }
                };
            }
        },
        fetch: async (url, options) => {
            requests.push({ url, options });
            const kind = await respond();
            if (kind === 'network') throw new Error('network');
            return { ok: kind !== 'http', redirected: kind === 'login', url: kind === 'login' ? '/auth/login' : url, text: async () => kind };
        }
    };
    const source = fs.readFileSync(path.join(__dirname, '../OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js'), 'utf8');
    // Expose the production closures only in this VM; no test API ships to the browser.
    vm.runInNewContext(source.replace(/\}\)\(\);\s*$/, 'window.testApi = { initDashboard, scheduleModuleFragmentRetry, loadModuleFragment }; })();'), context);
    const widget = { widgetId: 7 };
    return {
        timers, requests,
        get current() { return current; },
        init: () => context.window.testApi.initDashboard(root),
        schedule: () => context.window.testApi.scheduleModuleFragmentRetry(root, element, widget),
        load: () => context.window.testApi.loadModuleFragment(root, element, widget),
        detach: () => { attached = false; },
        respondWith: fn => { respond = fn; },
        async tick(expectedDelay) {
            assert.equal(timers.length, 1);
            const timer = timers.shift();
            assert.equal(timer.delay, expectedDelay);
            timer.callback();
            await new Promise(resolve => setImmediate(resolve));
        }
    };
}

test('server-rendered timeout retries after 2s and 5s, then shows the ordinary error', async () => {
    const d = dashboard();
    d.init();
    d.schedule();
    await d.tick(2000);
    assert.equal(d.current.placeholder.textContent, 'timeout');
    await d.tick(5000);
    d.schedule();
    assert.equal(d.requests.length, 2);
    assert.equal(d.timers.length, 0);
    assert.equal(d.current.dataset.moduleFragmentRetry, undefined);
    assert.equal(d.current.placeholder.textContent, 'Unavailable');
    assert.equal(d.current.classList.contains('is-unavailable'), true);
    assert.match(d.requests[0].url, /handler=ModuleFragment&widgetId=7&width=416/);
    assert.equal(d.requests[0].options.credentials, 'same-origin');
});

test('success on the first retry replaces the fragment and stops retrying', async () => {
    const d = dashboard('timeout', ['loaded']);
    d.init();
    await d.tick(2000);
    assert.equal(d.current.classList.contains('is-wide'), true);
    assert.equal(d.timers.length, 0);
    assert.equal(d.requests.length, 1);
});

test('ordinary failures and loaded widgets never schedule retries', () => {
    for (const kind of ['unavailable', 'loaded']) {
        const d = dashboard(kind);
        d.init();
        d.schedule();
        assert.equal(d.timers.length, 0);
        assert.equal(d.requests.length, 0);
    }
});

test('non-timeout responses, HTTP, login, malformed HTML and network failures stop retries', async () => {
    for (const kind of ['unavailable', 'http', 'login', 'invalid', 'network']) {
        const d = dashboard('timeout', [kind]);
        d.init();
        await d.tick(2000);
        assert.equal(d.timers.length, 0, kind);
        assert.equal(d.current.dataset.moduleFragmentRetry, undefined, kind);
        assert.equal(d.current.classList.contains('is-unavailable'), true, kind);
    }
});

test('a dynamically added widget uses the same two-retry budget', async () => {
    const d = dashboard('unavailable', ['timeout', 'timeout', 'timeout', 'timeout']);
    await d.load();
    await d.tick(2000);
    await d.tick(5000);
    assert.equal(d.requests.length, 3); // Initial request plus two retries.
    await d.load(); // Replacing markup again must not reset the page's retry budget.
    assert.equal(d.timers.length, 0);
    assert.equal(d.current.placeholder.textContent, 'Unavailable');
});

test('a pending timer or request prevents parallel requests for the same widget', async () => {
    const d = dashboard();
    let resolve;
    d.respondWith(() => new Promise(done => { resolve = done; }));
    d.init();
    await d.load();
    assert.equal(d.requests.length, 0);
    await d.tick(2000);
    d.schedule();
    await d.load();
    assert.equal(d.requests.length, 1);
    assert.equal(d.timers.length, 0);
    resolve('loaded');
    await new Promise(done => setImmediate(done));
    assert.equal(d.timers.length, 0);
});

test('removing a widget cancels its pending retry without making a request', async () => {
    const d = dashboard();
    d.init();
    d.detach();
    await d.tick(2000);
    assert.equal(d.requests.length, 0);
    assert.equal(d.timers.length, 0);
});
