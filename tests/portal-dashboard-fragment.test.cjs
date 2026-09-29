// Run with: node --test tests/portal-dashboard-fragment.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function dashboard(initial = 'timeout', responses = ['timeout', 'timeout']) {
    const timers = [];
    const requests = [];
    let respond = async () => responses.shift();
    function widgetElement(kind) {
        const element = {
            dataset: { widgetId: '7' },
            style: { width: '416px' },
            measuredWidth: 416,
            querySelector: () => element.current,
            getBoundingClientRect: () => ({ width: element.measuredWidth }),
            cloneNode: () => widgetElement(element.current.dataset.moduleFragmentRetry === 'timeout' ? 'timeout'
                : element.current.classList.contains('is-loading') ? 'loading'
                : element.current.classList.contains('is-unavailable') ? 'unavailable' : 'loaded')
        };
        element.current = fragment(kind);
        return element;
    }
    function fragment(kind) {
        const classes = new Set([['timeout', 'loading'].includes(kind) ? 'is-loading' : kind === 'loaded' ? 'is-wide' : 'is-unavailable']);
        const placeholder = { textContent: kind };
        return {
            dataset: { moduleFragmentWidgetId: '7', ...(kind === 'timeout' ? { moduleFragmentRetry: 'timeout' } : {}) },
            classList: { add: value => classes.add(value), remove: value => classes.delete(value), contains: value => classes.has(value) },
            querySelector: () => placeholder,
            replaceWith(value) {
                const owner = canvas.widgets.find(element => element.current === this);
                if (owner) owner.current = value;
            },
            set className(value) { value.split(' ').forEach(name => classes.add(name)); },
            setAttribute() {},
            appendChild(value) { this.placeholder = value; this.querySelector = () => value; },
            placeholder
        };
    }
    const canvas = {
        widgets: [widgetElement(initial)],
        querySelectorAll: () => canvas.widgets.slice(),
        appendChild: element => canvas.widgets.push(element)
    };
    const root = {
        dataset: { moduleFragmentUrl: '/?handler=ModuleFragment', moduleFragmentUnavailableLabel: 'Unavailable' },
        contains: element => canvas.widgets.includes(element),
        querySelector: () => null,
        querySelectorAll: selector => selector === '[data-dashboard-widget]' ? canvas.widgets : []
    };
    const context = {
        URL,
        window: {
            location: { href: 'http://localhost/' }, addEventListener() {},
            setTimeout: (callback, delay) => { const timer = { callback, delay }; timers.push(timer); return timer; },
            clearTimeout: timer => { const index = timers.indexOf(timer); if (index >= 0) timers.splice(index, 1); }
        },
        document: {
            readyState: 'loading', addEventListener() {},
            createElement: tag => {
                if (tag !== 'template') return fragment('loading');
                let value;
                return {
                    set innerHTML(kind) { value = kind === 'invalid' ? null : fragment(kind); },
                    content: { querySelector: () => value }
                };
            }
        },
        fetch: async (url, options) => {
            requests.push({ url, options });
            const kind = await respond(url);
            if (kind === 'network') throw new Error('network');
            return { ok: kind !== 'http', redirected: kind === 'login', url: kind === 'login' ? '/auth/login' : url, text: async () => kind };
        }
    };
    const source = fs.readFileSync(path.join(__dirname, '../OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js'), 'utf8');
    // Expose the production closures only in this VM; no test API ships to the browser.
    vm.runInNewContext(source.replace(/\}\)\(\);\s*$/, `
        // Stub unrelated layout, controls and persistence, retaining the production
        // reset/discard, draft, snapshot and fragment lifecycle functions.
        bindWidget = bindEntryFavoriteToggles = bindEntryListFilters = bindDashboardMusicPlayers =
            bindDashboardNotifications = clearDashboardBindingMarkers = updateWidgetSelectionState =
            updateEmptyState = updateCanvasHeight = updateAlignToGridState = updateExpandedCanvasState = () => {};
        getDashboardSignature = () => '';
        getMaxOrder = () => 1;
        removeDashboardWidgetElement = element => testRemove(element);
        createWidgetElement = (root, item) => testCreate(root, item, createModuleFragmentPlaceholder);
        window.testApi = { initDashboard, scheduleModuleFragmentRetry, loadModuleFragment,
            initializeModuleFragment, captureDashboardSnapshot, resetDashboardChanges, restoreDashboardDraft };
    })();`), Object.assign(context, {
        testRemove: element => { canvas.widgets = canvas.widgets.filter(item => item !== element); },
        testCreate: (root, item, placeholder) => {
            const element = widgetElement('loading');
            element.dataset.widgetId = String(item.widgetId);
            element.style.width = `${item.width || 320}px`;
            element.measuredWidth = 0; // Newly inserted widgets can be hidden before layout.
            element.current = placeholder(root);
            return element;
        }
    }));
    const widget = { widgetId: 7 };
    const api = context.window.testApi;
    const state = { addedWidgetIds: new Set(), pendingRemovedWidgetIds: new Set(), nextTemporaryWidgetId: -1 };
    return {
        timers, requests,
        get widgets() { return canvas.widgets; },
        duplicate: (kind) => { canvas.widgets.push(widgetElement(kind)); },
        get current() { return canvas.widgets[0]?.current; },
        init: () => api.initDashboard(root),
        schedule: () => api.scheduleModuleFragmentRetry(root, canvas.widgets[0], widget),
        load: () => api.loadModuleFragment(root, canvas.widgets[0], widget),
        detach: () => { canvas.widgets = []; },
        reinsert: (kind = 'loading') => { canvas.widgets = [widgetElement(kind)]; api.initializeModuleFragment(root, canvas.widgets[0]); },
        noEndpoint: () => { delete root.dataset.moduleFragmentUrl; },
        snapshot: () => api.captureDashboardSnapshot(canvas, state),
        reset: snapshot => api.resetDashboardChanges(root, canvas, '', state, snapshot, () => 1),
        restoreDraft: () => api.restoreDashboardDraft(root, canvas, '', state, {
            widgets: [{ widgetId: 7, widgetType: 'module-fragment', width: 528 }]
        }, () => {}),
        respondWith: fn => { respond = fn; },
        async tick(expectedDelay, expectedCount = 1) {
            assert.equal(timers.length, expectedCount);
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
    await d.load(); // Replacing fragment markup must not reset this element's retry budget.
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

test('reset/discard gives each cloned element its own bounded retry budget', async () => {
    const d = dashboard('timeout', ['timeout', 'timeout', 'timeout', 'timeout']);
    const snapshot = d.snapshot();
    d.init();
    await d.reset(snapshot); // Both Reset changes and Discard changes use this production path.
    await d.tick(2000, 2); // The detached element's timer exits without issuing a request.
    await d.tick(2000);
    await d.tick(5000);
    assert.equal(d.current.placeholder.textContent, 'Unavailable');
    await d.reset(snapshot);
    await d.tick(2000);
    await d.tick(5000);
    assert.equal(d.requests.length, 4);
    assert.equal(d.timers.length, 0);
    assert.equal(d.current.placeholder.textContent, 'Unavailable');
    assert.equal(d.current.classList.contains('is-loading'), false);
});

test('reset/discard reloads an initial loading snapshot and leaves loaded/error snapshots alone', async () => {
    for (const kind of ['loading', 'loaded', 'unavailable']) {
        const d = dashboard(kind, ['loaded']);
        await d.reset(d.snapshot());
        await new Promise(done => setImmediate(done));
        assert.equal(d.requests.length, kind === 'loading' ? 1 : 0, kind);
        assert.equal(d.current.classList.contains('is-loading'), false, kind);
    }
});

test('restoring a draft starts its fragment load with at most two timeout retries', async () => {
    const d = dashboard('loaded', ['timeout', 'timeout', 'timeout']);
    d.restoreDraft();
    await new Promise(done => setImmediate(done));
    assert.equal(d.requests.length, 1);
    assert.equal(new URL(d.requests[0].url).searchParams.get('width'), '528');
    await d.tick(2000);
    await d.tick(5000);
    assert.equal(d.requests.length, 3);
    assert.equal(d.current.placeholder.textContent, 'Unavailable');
    assert.equal(d.timers.length, 0);
});

test('reset/discard and draft restoration show an error when no fragment endpoint exists', async () => {
    for (const kind of ['timeout', 'loading']) {
        const d = dashboard(kind);
        d.noEndpoint();
        await d.reset(d.snapshot());
        assert.equal(d.current.placeholder.textContent, 'Unavailable');
        d.restoreDraft();
        assert.equal(d.current.placeholder.textContent, 'Unavailable');
        assert.equal(d.current.classList.contains('is-loading'), false);
        assert.equal(d.requests.length, 0);
        assert.equal(d.timers.length, 0);
    }
});

test('removing and readding the same widget leaves its old timer inert and loads the new element', async () => {
    const d = dashboard('timeout', ['loaded']);
    d.init();
    d.detach();
    d.reinsert();
    await new Promise(done => setImmediate(done));
    await d.tick(2000); // The detached element's callback cannot revive its work.
    assert.equal(d.requests.length, 1);
    assert.equal(d.timers.length, 0);
    assert.equal(d.current.classList.contains('is-wide'), true);
});

test('a superseded request cannot overwrite a new element or release its busy guard', async () => {
    const d = dashboard();
    const resolvers = [];
    d.respondWith(() => new Promise(done => resolvers.push(done)));
    d.init();
    await d.tick(2000);
    d.detach();
    d.reinsert();
    assert.equal(d.requests.length, 2);
    resolvers[0]('timeout');
    await new Promise(done => setImmediate(done));
    await d.load();
    assert.equal(d.requests.length, 2);
    assert.equal(d.timers.length, 0);
    resolvers[1]('timeout');
    await new Promise(done => setImmediate(done));
    await d.tick(2000);
    assert.equal(d.requests.length, 3);
    resolvers[2]('timeout');
    await new Promise(done => setImmediate(done));
    await d.tick(5000);
    assert.equal(d.requests.length, 4);
    resolvers[3]('timeout');
    await new Promise(done => setImmediate(done));
    assert.equal(d.current.placeholder.textContent, 'Unavailable');
    assert.equal(d.timers.length, 0);
});

for (const initial of ['timeout', 'loading']) {
    for (const outcome of ['loaded', 'timeout']) {
        test(`two instances of one widget independently finish: ${initial} -> ${outcome}`, async () => {
            const d = dashboard(initial);
            d.duplicate(initial);
            d.widgets[1].measuredWidth = 520;
            const counts = new Map();
            const expectedRequests = initial === 'timeout' ? 2 : 3;
            d.respondWith(async url => {
                const width = new URL(url).searchParams.get('width');
                const count = (counts.get(width) || 0) + 1;
                counts.set(width, count);
                return count === expectedRequests ? outcome : 'timeout';
            });
            d.init();
            await new Promise(done => setImmediate(done));
            await d.tick(2000, 2);
            await d.tick(2000, 2);
            await d.tick(5000, 2);
            await d.tick(5000);
            assert.deepEqual([...counts.entries()].sort(), [['416', expectedRequests], ['520', expectedRequests]]);
            assert.equal(d.requests.length, expectedRequests * 2);
            assert.equal(d.timers.length, 0);
            for (const element of d.widgets) {
                assert.equal(element.current.classList.contains('is-loading'), false);
                assert.equal(element.current.classList.contains(outcome === 'loaded' ? 'is-wide' : 'is-unavailable'), true);
                if (outcome === 'timeout') assert.equal(element.current.placeholder.textContent, 'Unavailable');
            }
        });
    }
}
