// Run with: node --test tests/time-presentation.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function clientTime(zone, asset = 'portal-topbar.js', fallbackZone = null, warnings = [], calendarZone = null) {
    const context = {
        console: { warn(message) { warnings.push(message); } },
        window: { addEventListener() {} },
        document: {
            documentElement: { lang: 'en-GB' },
            readyState: 'loading',
            addEventListener() {},
            querySelector(selector) {
                if (selector.includes('omp-calendar-time-zone')) {
                    return calendarZone === null ? null : { getAttribute: () => calendarZone };
                }
                const value = selector.includes('meta')
                    ? (zone ?? (selector.includes('[data-omp-time-zone]') ? fallbackZone : null))
                    : fallbackZone;
                return value === null ? null : { getAttribute: () => value };
            }
        }
    };
    vm.createContext(context);
    vm.runInContext(fs.readFileSync(path.join(__dirname,
        '../OpenModulePlatform.Web.Shared/wwwroot/js', asset), 'utf8'), context);
    return context.window;
}

const formatter = zone => clientTime(zone).OmpTime.formatUtc;

test('summer and winter use the configured calendar, regardless of browser zone', () => {
    process.env.TZ = 'America/Los_Angeles';
    const format = formatter('Europe/Stockholm');
    assert.match(format('2026-09-24T22:30:00Z'), /25\/09\/2026.*00:30:00 CEST/);
    assert.match(format('2026-01-14T23:30:00Z'), /15\/01\/2026.*00:30:00 CET/);
    assert.equal(format('2026-09-24T22:30:00'), format('2026-09-24T22:30:00Z'));
    assert.equal(format('2026-09-25T00:30:00+02:00'), format('2026-09-24T22:30:00Z'));
});

test('picker warns once when metadata is missing and uses the server data attribute', () => {
    const warnings = [];
    const now = clientTime(null, 'omp-datetime.js', 'Europe/Stockholm', warnings).ompDatetime.calendarNow;
    assert.equal(now(new Date('2026-09-24T22:30:00Z')).toISOString(), '2026-09-25T00:30:00.000Z');
    now(new Date('2026-01-14T23:30:00Z'));
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /omp-time-zone.*Europe\/Stockholm/);
});

test('picker warns about UTC when both metadata sources are absent', () => {
    const warnings = [];
    const now = clientTime(null, 'omp-datetime.js', null, warnings).ompDatetime.calendarNow;
    assert.equal(now(new Date('2026-09-24T22:30:00Z')).toISOString(), '2026-09-24T22:30:00.000Z');
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /UTC/);
});

test('picker prefers valid metadata and treats empty metadata as missing', () => {
    const warnings = [];
    const instant = new Date('2026-09-24T22:30:00Z');
    assert.equal(clientTime('UTC', 'omp-datetime.js', 'Europe/Stockholm', warnings)
        .ompDatetime.calendarNow(instant).toISOString(), instant.toISOString());
    assert.equal(warnings.length, 0);
    assert.equal(clientTime('', 'omp-datetime.js', 'Europe/Stockholm', warnings)
        .ompDatetime.calendarNow(instant).toISOString(), '2026-09-25T00:30:00.000Z');
    assert.equal(warnings.length, 1);
    assert.throws(() => clientTime(null, 'omp-datetime.js', 'Invalid/Zone')
        .ompDatetime.calendarNow(instant), { name: 'RangeError' });
});

test('UTC remains UTC and invalid configured zones never fall back to the browser', () => {
    assert.match(formatter('UTC')('2026-09-24T22:30:00Z'), /24\/09\/2026.*22:30:00 UTC/);
    assert.throws(() => formatter('Invalid/Zone')('2026-09-24T22:30:00Z'), { name: 'RangeError' });
    assert.equal(formatter('UTC')(null), '');
    assert.equal(formatter('UTC')('not-a-date'), '');
});

test('picker Today, Now and relative limits share the configured local calendar', () => {
    process.env.TZ = 'America/Los_Angeles';
    const now = clientTime('Europe/Stockholm', 'omp-datetime.js').ompDatetime.calendarNow;
    assert.equal(now(new Date('2026-09-24T22:30:00Z')).toISOString(), '2026-09-25T00:30:00.000Z');
    assert.equal(now(new Date('2026-01-14T23:30:00Z')).toISOString(), '2026-01-15T00:30:00.000Z');
    // The result is a calendar carrier; only its UTC fields represent the UI date/time.
    assert.equal(clientTime('UTC', 'omp-datetime.js').ompDatetime.calendarNow(new Date('2026-09-24T22:30:00Z')).getUTCDate(), 24);
});

// One page, both assets: the same document serves formatUtc and the picker.
function page(zone, calendarZone, warnings = []) {
    const win = clientTime(zone, 'portal-topbar.js', null, warnings, calendarZone);
    const picker = clientTime(zone, 'omp-datetime.js', null, warnings, calendarZone).ompDatetime;
    return { formatUtc: win.OmpTime.formatUtc, calendarNow: picker.calendarNow };
}

test('without omp-calendar-time-zone the picker and formatUtc share omp-time-zone', () => {
    process.env.TZ = 'America/Los_Angeles';
    const warnings = [];
    const { formatUtc, calendarNow } = page('UTC', null, warnings);
    assert.equal(calendarNow(new Date('2026-09-24T22:30:00Z')).toISOString(), '2026-09-24T22:30:00.000Z');
    assert.match(formatUtc('2026-09-24T22:30:00Z'), /24\/09\/2026.*22:30:00 UTC/);
    // An empty calendar tag is the same as no tag.
    assert.equal(page('UTC', '', warnings).calendarNow(new Date('2026-09-24T22:30:00Z')).toISOString(),
        '2026-09-24T22:30:00.000Z');
    assert.equal(warnings.length, 0);
});

test('omp-calendar-time-zone moves the picker calendar but never formatUtc', () => {
    process.env.TZ = 'America/Los_Angeles';
    const warnings = [];
    const { formatUtc, calendarNow } = page('UTC', 'Europe/Stockholm', warnings);
    // Local 00:30 in summer and winter is already the next business day.
    assert.equal(calendarNow(new Date('2026-09-24T22:30:00Z')).toISOString(), '2026-09-25T00:30:00.000Z');
    assert.equal(calendarNow(new Date('2026-01-14T23:30:00Z')).toISOString(), '2026-01-15T00:30:00.000Z');
    // The presentation zone stays UTC for the same page.
    assert.match(formatUtc('2026-09-24T22:30:00Z'), /24\/09\/2026.*22:30:00 UTC/);
    assert.equal(warnings.length, 0);
});

test('omp-calendar-time-zone wins without omp-time-zone and invalid values fail explicitly', () => {
    const warnings = [];
    const instant = new Date('2026-09-24T22:30:00Z');
    assert.equal(clientTime(null, 'omp-datetime.js', 'UTC', warnings, 'Europe/Stockholm')
        .ompDatetime.calendarNow(instant).toISOString(), '2026-09-25T00:30:00.000Z');
    assert.equal(warnings.length, 0);
    assert.throws(() => clientTime('UTC', 'omp-datetime.js', null, [], 'Invalid/Zone')
        .ompDatetime.calendarNow(instant), { name: 'RangeError' });
});
