// Run with: node --test tests/portal-dashboard-notification-restore.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// A small DOM fixture: deep clones copy attributes and children, never listeners.
// No network, browser profile, server or production test exports are used.
class Element {
    constructor(tag = 'div', dataset = {}) {
        this.tagName = tag;
        this.dataset = { ...dataset };
        this.children = [];
        this.listeners = new Map();
        this.classes = new Set();
        this.classList = {
            add: value => this.classes.add(value), remove: value => this.classes.delete(value),
            contains: value => this.classes.has(value)
        };
    }
    set className(value) { this.classes = new Set(value.split(/\s+/)); }
    appendChild(child) { child.parent = this; this.children.push(child); return child; }
    append(...children) { children.forEach(child => this.appendChild(child)); }
    remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
    setAttribute() {}
    matches(selector) {
        if (selector === 'button[type="submit"]') return this.tagName === 'button' && this.type === 'submit';
        const match = /^\[data-([a-z-]+)\]$/.exec(selector);
        return !!match && match[1].replace(/-([a-z])/g, (_, letter) => letter.toUpperCase()) in this.dataset;
    }
    querySelectorAll(selector) {
        return this.children.flatMap(child => [
            ...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)
        ]);
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    closest(selector) { return this.matches(selector) ? this : this.parent?.closest(selector); }
    addEventListener(name, handler) {
        if (!this.listeners.has(name)) this.listeners.set(name, []);
        this.listeners.get(name).push(handler);
    }
    async fire(name) {
        const event = { preventDefault() {}, stopPropagation() {} };
        await Promise.all((this.listeners.get(name) || []).map(handler => handler(event)));
        await new Promise(resolve => setImmediate(resolve));
    }
    cloneNode(deep) {
        const copy = new Element(this.tagName, this.dataset);
        copy.classes = new Set(this.classes);
        for (const property of ['type', 'action', 'scrollTop', 'scrollHeight', 'clientHeight']) {
            copy[property] = this[property];
        }
        if (deep) this.children.forEach(child => copy.appendChild(child.cloneNode(true)));
        return copy;
    }
}

function fixture(canUseNotifications = true) {
    const requests = [];
    const events = [];
    const root = new Element('div', { dashboardRoot: '' });
    const canvas = root.appendChild(new Element('div'));
    const widget = canvas.appendChild(new Element('section', { dashboardWidget: '', userActiveWidgetId: '101' }));
    const feed = widget.appendChild(new Element('div', {
        dashboardNotificationFeed: '', canUseNotifications: String(canUseNotifications),
        loadUrl: '/notifications/list', pageSize: '20', markReadUrl: '/notifications/read'
    }));
    feed.scrollTop = 150;
    feed.clientHeight = 200;
    feed.scrollHeight = 400;
    const list = feed.appendChild(new Element('div', { dashboardNotificationList: '' }));
    const form = list.appendChild(new Element('form', {
        dashboardNotificationForm: '', notificationId: '17', notificationCreatedAt: '2026-01-01T00:00:00Z'
    }));
    form.action = '/notifications/read';
    form.classList.add('is-unread');
    const button = form.appendChild(new Element('button'));
    button.type = 'submit';
    const state = { alignToGrid: true, expandedCanvas: true, addedWidgetIds: new Set(), pendingRemovedWidgetIds: new Set() };
    const source = fs.readFileSync(path.join(__dirname, '../OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js'), 'utf8');
    const context = {
        URL, FormData,
        CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
        window: { location: { href: 'http://fixture.invalid/' }, addEventListener() {}, dispatchEvent: event => events.push(event) },
        document: { readyState: 'loading', addEventListener() {}, createElement: tag => new Element(tag) },
        fetch: async (url, options) => {
            requests.push({ url, options });
            return {
                ok: true, status: 200, headers: { get: () => 'application/json' },
                json: async () => options.method === 'POST' ? { unreadCount: 0 } : {
                    items: [{ notificationId: 18, title: 'Next', isUnread: true }], hasMore: true
                }
            };
        }
    };
    vm.runInNewContext(source.replace(/\}\)\(\);\s*$/, `
        // Keep production capture/restore/reset, binding, POST, paging and row updates.
        // Only unrelated controls, fragments, layout and persistence are inert.
        bindWidget = bindEntryFavoriteToggles = bindEntryListFilters = bindDashboardMusicPlayers =
            initializeModuleFragment = updateWidgetSelectionState = updateEmptyState = updateCanvasHeight = () => {};
        getDashboardSignature = () => '';
        getMaxOrder = () => 1;
        removeDashboardWidgetElement = element => element.remove();
        window.testApi = { bindDashboardNotifications, captureDashboardSnapshot, resetDashboardChanges };
    })();`), context);
    const api = context.window.testApi;
    const currentFeed = () => canvas.querySelector('[data-dashboard-notification-feed]');
    api.bindDashboardNotifications(root);
    return {
        root, canvas, requests, events, api, state, currentFeed,
        snapshot: () => api.captureDashboardSnapshot(canvas, state),
        reset: snapshot => api.resetDashboardChanges(root, canvas, '', state, snapshot, () => 1),
        bindAgain: () => api.bindDashboardNotifications(root)
    };
}

test('reset/discard restores working mark-read and paging handlers exactly once on every clone', async () => {
    const d = fixture();
    let snapshot = d.snapshot();
    for (let lap = 0; lap < 3; lap++) {
        const oldFeed = d.currentFeed();
        await d.reset(snapshot);
        const feed = d.currentFeed();
        assert.notEqual(feed, oldFeed);
        d.bindAgain(); // Ordinary init calls must be idempotent after restoration.
        const form = feed.querySelector('[data-dashboard-notification-form]');
        assert.equal(form.listeners.get('submit')?.length, 1, 'restored form needs exactly one live listener');
        assert.equal(form.querySelector('button[type="submit"]').listeners.get('click')?.length, 1);
        assert.equal(feed.listeners.get('scroll')?.length, 1);
        if (lap % 2) await form.fire('submit');
        else await form.querySelector('button[type="submit"]').fire('click');
        assert.equal(d.requests.filter(request => request.options.method === 'POST').length, lap + 1);
        assert.equal(form.classList.contains('is-read'), true);
        assert.equal(form.classList.contains('is-unread'), false);
        assert.equal(d.events.length, lap + 1);
        assert.equal(d.events[lap].detail.notificationId, '17');
        assert.equal(d.requests.at(-1).options.body.get('notificationId'), '17');
        await feed.fire('scroll');
        const pages = d.requests.filter(request => request.options.method !== 'POST');
        assert.equal(pages.length, lap + 1);
        assert.equal(new URL(pages.at(-1).url).searchParams.get('beforeNotificationId'), '17');
        const added = feed.querySelectorAll('[data-dashboard-notification-form]')[1];
        assert.equal(added.dataset.notificationId, '18');
        assert.equal(added.listeners.get('submit')?.length, 1, 'new page rows must also be bound');
        snapshot = d.snapshot();
        // Return the saved fixture to one row to keep the cursor assertions stable.
        snapshot.widgets[0].querySelectorAll('[data-dashboard-notification-form]')[1].remove();
    }
});

test('restoring an in-flight snapshot clears copied busy flags and resumes form and scroll work', async () => {
    const d = fixture();
    const feed = d.currentFeed();
    feed.dataset.notificationLoading = 'true';
    feed.querySelector('[data-dashboard-notification-form]').dataset.dashboardNotificationSubmitting = 'true';
    await d.reset(d.snapshot());
    await d.currentFeed().querySelector('[data-dashboard-notification-form]').fire('submit');
    await d.currentFeed().fire('scroll');
    assert.equal(d.requests.length, 2, 'copied loading/submitting flags must not strand the clone');
});

test('an unavailable notification feed is restored without enabling scroll fetches', async () => {
    const d = fixture(false);
    await d.reset(d.snapshot());
    await d.currentFeed().fire('scroll');
    assert.equal(d.requests.length, 0);
});
