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
        for (const selected of [1, 0]) {
            tabs[selected].listeners.click();
            for (let j = 0; j < 2; j++) {
                assert.equal(tabs[j].getAttribute('aria-selected'), String(j === selected));
                assert.equal(panes[j].hidden, j !== selected);
            }
        }
    }
});
