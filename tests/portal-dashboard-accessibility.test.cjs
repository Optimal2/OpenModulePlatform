// Run with: node --test tests/portal-dashboard-accessibility.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function element() {
    return {
        dataset: {}, children: [], attributes: {}, listeners: {},
        classList: { toggle() {} },
        append(...children) { this.children.push(...children); },
        appendChild(child) { this.append(child); },
        setAttribute(name, value) { this.attributes[name] = value; },
        getAttribute(name) { return this.attributes[name]; },
        addEventListener(name, callback) { this.listeners[name] = callback; },
        focus() { this.focusCount = (this.focusCount || 0) + 1; },
        querySelectorAll(selector) {
            const key = selector === '[data-blank-widget-admin-tab]'
                ? 'blankWidgetAdminTab' : 'blankWidgetAdminPane';
            return this.children.flatMap(child => [
                ...(key in child.dataset ? [child] : []), ...child.querySelectorAll(selector)
            ]);
        }
    };
}

test('new image widgets have unique, connected tab panels and preserve selection', () => {
    const source = fs.readFileSync(path.join(__dirname, '../OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js'), 'utf8');
    const context = {
        window: { addEventListener() {} },
        document: { readyState: 'loading', addEventListener() {}, createElement: element }
    };
    vm.runInNewContext(source.replace(/\}\)\(\);\s*$/, `
        window.testApi = { createBlankWidgetAdminControls, bindBlankWidgetAdminTabs };
    })();`), context);
    const ids = new Set();
    for (let i = 0; i < 2; i++) {
        const panel = context.window.testApi.createBlankWidgetAdminControls({ dataset: {} });
        const tabs = panel.querySelectorAll('[data-blank-widget-admin-tab]');
        const panes = panel.querySelectorAll('[data-blank-widget-admin-pane]');
        assert.equal(tabs.length, 2);
        assert.equal(panes.length, 2);
        for (let j = 0; j < 2; j++) {
            const tab = tabs[j];
            const pane = panes[j];
            assert.ok(tab.id, 'tab needs an ID');
            assert.ok(pane.id, 'panel needs an ID');
            assert.equal(ids.has(tab.id), false);
            assert.equal(ids.has(pane.id), false);
            ids.add(tab.id);
            ids.add(pane.id);
            assert.equal(tab.getAttribute('aria-controls'), pane.id);
            assert.equal(pane.getAttribute('aria-labelledby'), tab.id);
            assert.equal(pane.getAttribute('role'), 'tabpanel');
        }
        context.window.testApi.bindBlankWidgetAdminTabs(panel);
        assert.equal(tabs[0].tabIndex, 0);
        assert.equal(tabs[1].tabIndex, -1);
        assert.equal(tabs[0].focusCount, undefined, 'binding must not steal focus');
        for (const selected of [1, 0]) {
            tabs[selected].listeners.click();
            for (let j = 0; j < 2; j++) {
                assert.equal(tabs[j].getAttribute('aria-selected'), String(j === selected));
                assert.equal(panes[j].hidden, j !== selected);
                assert.equal(tabs[j].tabIndex, j === selected ? 0 : -1);
            }
        }
        for (const [from, key, selected] of [
            [0, 'ArrowRight', 1], [1, 'ArrowRight', 0],
            [0, 'ArrowLeft', 1], [1, 'ArrowLeft', 0],
            [0, 'End', 1], [1, 'Home', 0]
        ]) {
            let prevented = false;
            const previousFocusCount = tabs[selected].focusCount || 0;
            tabs[from].listeners.keydown({ key, preventDefault() { prevented = true; } });
            assert.equal(prevented, true);
            assert.equal(tabs[selected].focusCount, previousFocusCount + 1);
            for (let j = 0; j < 2; j++) {
                assert.equal(tabs[j].getAttribute('aria-selected'), String(j === selected));
                assert.equal(tabs[j].tabIndex, j === selected ? 0 : -1);
                assert.equal(panes[j].hidden, j !== selected);
            }
        }
        for (const key of ['Tab', 'ArrowDown', 'ArrowUp', 'a']) {
            tabs[0].listeners.keydown({ key, preventDefault() { assert.fail(`${key} must retain its default action`); } });
            assert.equal(tabs[0].getAttribute('aria-selected'), 'true');
        }
    }
});

test('isolated CSS cascade preserves collapsed values and measures unresolved tab backgrounds', async () => {
    // Reuse the repository's pinned, already-installed Playwright tooling. No
    // download, application server, shared browser or external request is used.
    const os = require('node:os');
    const scratch = process.env.AO_JOB_SCRATCH || 'G:\\AI_Workdir';
    process.env.TMP = scratch;
    process.env.TEMP = scratch;
    process.env.PLAYWRIGHT_BROWSERS_PATH ||= path.join(os.homedir(), 'AppData/Local/ms-playwright');
    const packages = fs.readFileSync(path.join(__dirname, '../Directory.Packages.props'), 'utf8');
    const version = /Include="Microsoft.Playwright" Version="([^"]+)"/.exec(packages)[1];
    const modulePath = path.join(process.env.NUGET_PACKAGES || path.join(os.homedir(), '.nuget/packages'),
        'microsoft.playwright', version, '.playwright/package');
    const { chromium } = require(modulePath);
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage();
        await page.route('**/*', route => route.abort());
        const portal = fs.readFileSync(path.join(__dirname, '../OpenModulePlatform.Portal/wwwroot/css/site.css'), 'utf8');
        const shared = fs.readFileSync(path.join(__dirname, '../OpenModulePlatform.Web.Shared/wwwroot/css/omp-theme.css'), 'utf8');
        for (const palette of ['absent', 'light', 'dark']) {
            await page.setContent(`<html data-theme="${palette}"><head><style>${palette === 'absent' ? '' : shared}\n${portal}</style></head>
                <body><section class="maintenance-preview-candidate-card is-collapsed">
                <div class="maintenance-preview-candidate-card__summary"><span id="summary">Count</span></div></section>
                <div class="dashboard-music-player__admin-tabs" id="music"></div>
                <div class="dashboard-widget__blank-admin-tabs" id="image"></div></body></html>`);
            const values = await page.evaluate(() => {
                const summary = getComputedStyle(document.querySelector('#summary'));
                return {
                    rootSize: parseFloat(getComputedStyle(document.documentElement).fontSize),
                    summarySize: parseFloat(summary.fontSize),
                    summaryColor: summary.color,
                    missingToken: getComputedStyle(document.documentElement).getPropertyValue('--surface-alt').trim(),
                    backgrounds: ['music', 'image'].map(id => getComputedStyle(document.getElementById(id)).backgroundColor)
                };
            });
            assert.ok(Math.abs(values.summarySize - values.rootSize * 1.05) < 0.001, palette);
            assert.equal(values.summaryColor, palette === 'dark' ? 'rgb(182, 195, 213)' : 'rgb(91, 102, 122)', palette);
            assert.equal(values.missingToken, '', palette);
            assert.deepEqual(values.backgrounds, ['rgba(0, 0, 0, 0)', 'rgba(0, 0, 0, 0)'], palette);
        }
    } finally {
        await browser.close();
    }
});
