// Run with: node --test tests/time-presentation.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function clientTime(zone, asset = 'portal-topbar.js') {
    const context = {
        window: { addEventListener() {} },
        document: {
            documentElement: { lang: 'en-GB' },
            readyState: 'loading',
            addEventListener() {},
            querySelector() {
                return { getAttribute: () => zone };
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
