// File: scripts/js-tests/live-refresh.tests.js
// Headless (jsdom) tests for OpenModulePlatform.Web.Shared/wwwroot/js/omp-live-refresh.js.
// Run via scripts/js-tests/run-js-tests.ps1 (installs jsdom on first run).
'use strict';

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');

const helperSource = fs.readFileSync(
    path.join(__dirname, '..', '..', 'OpenModulePlatform.Web.Shared', 'wwwroot', 'js', 'omp-live-refresh.js'),
    'utf8');

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function createPage({ topbarMode = 'push' } = {}) {
    const dom = new JSDOM(
        `<html><head></head><body>
            <div data-portal-topbar-root
                 data-notification-update-mode="${topbarMode}"
                 data-notification-push-url="/topbar/notifications/updates"></div>
        </body></html>`,
        { runScripts: 'outside-only', pretendToBeVisual: true, url: 'http://localhost:8088/module/' });

    const { window } = dom;
    window.eval(helperSource);

    return {
        window: window,
        setChannelConnected(connected) {
            window.ompPushChannel = { connected: connected };
            window.dispatchEvent(new window.CustomEvent('omp:push-channel-state', {
                detail: { connected: connected }
            }));
        },
        pushEnvelope(category, payload, eventId) {
            window.dispatchEvent(new window.CustomEvent('omp:push-event', {
                detail: {
                    envelope: { eventId: eventId, category: category, payload: payload },
                    category: category,
                    payload: payload
                }
            }));
        }
    };
}

const tests = [];
function test(name, fn) {
    tests.push({ name, fn });
}

test('push event for the subscribed module triggers one debounced refresh', async () => {
    const page = createPage();
    const calls = [];
    page.window.ompLiveRefresh.subscribe({
        module: 'ibs_packager',
        debounceMs: 20,
        onRefresh: (info) => calls.push(info.source)
    });

    page.pushEnvelope('module.state-changed', { module: 'ibs_packager' }, 'e1');
    page.pushEnvelope('module.state-changed', { module: 'ibs_packager' }, 'e2');
    await sleep(60);
    assert.deepStrictEqual(calls, ['push'], 'two rapid events must collapse into one refresh');
});

test('events for other modules or categories are ignored', async () => {
    const page = createPage();
    const calls = [];
    page.window.ompLiveRefresh.subscribe({
        module: 'ibs_packager',
        debounceMs: 10,
        onRefresh: () => calls.push(1)
    });

    page.pushEnvelope('module.state-changed', { module: 'earkiv_checker' }, 'e1');
    page.pushEnvelope('some.other-category', { module: 'ibs_packager' }, 'e2');
    await sleep(40);
    assert.strictEqual(calls.length, 0);
});

test('fallback polls while no live channel exists and stops when push connects', async () => {
    const page = createPage();
    const sources = [];
    page.window.ompLiveRefresh.subscribe({
        module: 'ibs_packager',
        fallback: { intervalMs: 30 },
        onRefresh: (info) => sources.push(info.source)
    });

    await sleep(100);
    assert.ok(sources.length >= 2, 'expected at least two fallback polls, got ' + sources.length);
    assert.ok(sources.every((source) => source === 'fallback'));

    page.setChannelConnected(true);
    const countWhenLive = sources.length;
    await sleep(100);
    assert.strictEqual(sources.length, countWhenLive, 'fallback must stop while the channel is live');

    page.setChannelConnected(false);
    await sleep(100);
    assert.ok(sources.length > countWhenLive, 'fallback must resume when the channel drops');
});

test('onStateChange reports live transitions with the transport', async () => {
    const page = createPage();
    const states = [];
    page.window.ompLiveRefresh.subscribe({
        module: 'ibs_packager',
        onRefresh: () => { },
        onStateChange: (state) => states.push(state.live + ':' + state.transport)
    });

    assert.deepStrictEqual(states, ['false:none'], 'initial state must be reported');
    page.setChannelConnected(true);
    page.setChannelConnected(true); // duplicate must not re-notify
    page.setChannelConnected(false);
    assert.deepStrictEqual(states, ['false:none', 'true:topbar', 'false:none']);
});

test('unsubscribe stops both push refreshes and fallback polling', async () => {
    const page = createPage();
    const calls = [];
    const handle = page.window.ompLiveRefresh.subscribe({
        module: 'ibs_packager',
        debounceMs: 10,
        fallback: { intervalMs: 25 },
        onRefresh: () => calls.push(1)
    });

    handle.unsubscribe();
    page.pushEnvelope('module.state-changed', { module: 'ibs_packager' }, 'e1');
    await sleep(80);
    assert.strictEqual(calls.length, 0);
});

test('refreshNow invokes the callback with source manual', () => {
    const page = createPage();
    const sources = [];
    const handle = page.window.ompLiveRefresh.subscribe({
        module: 'ibs_packager',
        onRefresh: (info) => sources.push(info.source)
    });

    handle.refreshNow();
    assert.deepStrictEqual(sources, ['manual']);
});

test('requestPush without a topbar channel attempts a page-owned connection', async () => {
    const page = createPage({ topbarMode: 'poll' });
    page.window.ompLiveRefresh.subscribe({
        module: 'earkiv_checker',
        requestPush: true,
        fallback: { intervalMs: 30 },
        onRefresh: () => { }
    });

    await sleep(30);
    const script = page.window.document.querySelector('script[src*="signalr.min.js"]');
    assert.ok(script, 'the SignalR client script must be requested for a page-owned connection');
});

test('a connected topbar channel suppresses the page-owned connection', async () => {
    const page = createPage({ topbarMode: 'push' });
    page.setChannelConnected(true);
    page.window.ompLiveRefresh.subscribe({
        module: 'earkiv_checker',
        requestPush: true,
        onRefresh: () => { }
    });

    await sleep(30);
    const script = page.window.document.querySelector('script[src*="signalr.min.js"]');
    assert.strictEqual(script, null, 'no second connection while the topbar channel is live');
});

test('subscribe validates the onRefresh callback', () => {
    const page = createPage();
    assert.throws(() => page.window.ompLiveRefresh.subscribe({ module: 'x' }), /onRefresh/);
});

(async () => {
    let failed = 0;
    for (const { name, fn } of tests) {
        try {
            await fn();
            console.log('PASS  ' + name);
        } catch (error) {
            failed += 1;
            console.error('FAIL  ' + name);
            console.error('      ' + (error && error.message ? error.message : error));
        }
    }

    console.log('');
    console.log(failed === 0
        ? 'JS tests passed (' + tests.length + '/' + tests.length + ').'
        : 'JS tests FAILED (' + (tests.length - failed) + '/' + tests.length + ' passed).');
    process.exit(failed === 0 ? 0 : 1);
})();
