/**
 * Tests for bindBpsInput helper in admin-forms.js
 *
 * Red-phase tests: bindBpsInput does not yet exist.
 * All tests are expected to FAIL until the implementation is added.
 */

// Load the source file into the jsdom global scope
const fs = require('fs');
const path = require('path');

const SOURCE_FILE = path.resolve(
  __dirname,
  '../../src/FundingRateArb.Web/wwwroot/js/admin-forms.js'
);

function loadSource() {
  const src = fs.readFileSync(SOURCE_FILE, 'utf8');
  // Indirect eval runs at global scope — function declarations become global properties
  // eslint-disable-next-line no-eval
  global.eval(src);
}

function makeElements(visibleId, rawId, rawValue = '') {
  document.body.innerHTML = `
    <div class="form-group">
      <input id="${visibleId}" type="number" />
      <input id="${rawId}"   type="hidden" value="${rawValue}" />
    </div>
  `;
  return {
    visible: document.getElementById(visibleId),
    raw: document.getElementById(rawId),
  };
}

function fireEvent(element, eventName) {
  element.dispatchEvent(new Event(eventName, { bubbles: true }));
}

beforeEach(() => {
  document.body.innerHTML = '';
  // Re-evaluate source so globals are fresh per test
  loadSource();
});

// ---------------------------------------------------------------------------
// 1. bindBpsInput exists and is a global function
// ---------------------------------------------------------------------------
describe('bindBpsInput — existence', () => {
  test('bindBpsInput is defined as a global function', () => {
    expect(typeof globalThis.bindBpsInput).toBe('function');
  });
});

// ---------------------------------------------------------------------------
// 2. Default factor is 10000
// ---------------------------------------------------------------------------
describe('bindBpsInput — default factor', () => {
  test('default factor of 10000: visible = raw / 10000 on init', () => {
    const { visible, raw } = makeElements('vis', 'raw', '0.0002');
    // Call without third argument — should use default factor 10000
    globalThis.bindBpsInput('vis', 'raw');
    // 0.0002 / 10000 = 2e-8; must not be empty or "0"
    expect(visible.value).not.toBe('');
    expect(visible.value).not.toBe('0');
    const numeric = parseFloat(visible.value);
    expect(numeric).toBeCloseTo(0.0002 / 10000, 12);
  });

  test('default factor of 10000: input event multiplies visible × 10000 into raw', () => {
    const { visible, raw } = makeElements('vis', 'raw', '0');
    globalThis.bindBpsInput('vis', 'raw');
    visible.value = '3';
    fireEvent(visible, 'input');
    expect(parseFloat(raw.value)).toBeCloseTo(3 * 10000, 6);
  });
});

// ---------------------------------------------------------------------------
// 3. DOM-ready initialisation: visible = raw / factor
// ---------------------------------------------------------------------------
describe('bindBpsInput — DOM-ready initialisation', () => {
  test('populates visible field as raw / factor on bind', () => {
    const { visible, raw } = makeElements('vis', 'raw', '0.0002');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    const expected = 0.0002 / 10000;
    expect(parseFloat(visible.value)).toBeCloseTo(expected, 12);
  });

  test('formats with sufficient precision — 0.0002/10000 does not display as "0"', () => {
    const { visible } = makeElements('vis', 'raw', '0.0002');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    expect(visible.value).not.toBe('0');
    expect(visible.value).not.toBe('');
    // Must contain a non-zero digit after the decimal
    expect(parseFloat(visible.value)).toBeGreaterThan(0);
  });

  test('does not populate visible when raw is empty', () => {
    const { visible } = makeElements('vis', 'raw', '');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    expect(visible.value).toBe('');
  });

  test('works with a custom factor', () => {
    const { visible } = makeElements('vis', 'raw', '100');
    globalThis.bindBpsInput('vis', 'raw', 200);
    expect(parseFloat(visible.value)).toBeCloseTo(100 / 200, 8);
  });
});

// ---------------------------------------------------------------------------
// 4. input event: raw = parseFloat(visible) * factor
// ---------------------------------------------------------------------------
describe('bindBpsInput — input event handler', () => {
  test('input event updates raw to visible * factor', () => {
    const { visible, raw } = makeElements('vis', 'raw', '0');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    visible.value = '5';
    fireEvent(visible, 'input');
    expect(parseFloat(raw.value)).toBeCloseTo(5 * 10000, 6);
  });

  test('input event with fractional visible value', () => {
    const { visible, raw } = makeElements('vis', 'raw', '0');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    visible.value = '0.5';
    fireEvent(visible, 'input');
    expect(parseFloat(raw.value)).toBeCloseTo(0.5 * 10000, 6);
  });

  test('input event respects custom factor', () => {
    const { visible, raw } = makeElements('vis', 'raw', '0');
    globalThis.bindBpsInput('vis', 'raw', 50);
    visible.value = '4';
    fireEvent(visible, 'input');
    expect(parseFloat(raw.value)).toBeCloseTo(4 * 50, 6);
  });
});

// ---------------------------------------------------------------------------
// 5. change event: raw = parseFloat(visible) * factor
// ---------------------------------------------------------------------------
describe('bindBpsInput — change event handler', () => {
  test('change event updates raw to visible * factor', () => {
    const { visible, raw } = makeElements('vis', 'raw', '0');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    visible.value = '7';
    fireEvent(visible, 'change');
    expect(parseFloat(raw.value)).toBeCloseTo(7 * 10000, 6);
  });
});

// ---------------------------------------------------------------------------
// 6. NaN guard: leave raw UNCHANGED when parse fails
// ---------------------------------------------------------------------------
describe('bindBpsInput — NaN guard', () => {
  test('input of non-numeric string leaves raw value unchanged', () => {
    const { visible, raw } = makeElements('vis', 'raw', '99');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    const originalRaw = raw.value;
    visible.value = 'abc';
    fireEvent(visible, 'input');
    // raw must NOT be cleared — it must retain its previous value
    expect(raw.value).toBe(originalRaw);
  });

  test('change event with non-numeric string leaves raw unchanged', () => {
    const { visible, raw } = makeElements('vis', 'raw', '42');
    globalThis.bindBpsInput('vis', 'raw', 10000);
    const originalRaw = raw.value;
    visible.value = '';
    fireEvent(visible, 'change');
    expect(raw.value).toBe(originalRaw);
  });
});

// ---------------------------------------------------------------------------
// 7. Missing elements: function returns without throwing
// ---------------------------------------------------------------------------
describe('bindBpsInput — missing elements', () => {
  test('does not throw when visible element does not exist', () => {
    makeElements('vis', 'raw', '1');
    expect(() => globalThis.bindBpsInput('nonexistent', 'raw', 10000)).not.toThrow();
  });

  test('does not throw when raw element does not exist', () => {
    makeElements('vis', 'raw', '1');
    expect(() => globalThis.bindBpsInput('vis', 'nonexistent', 10000)).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// 8. bindPercentInput still works (no regression)
// ---------------------------------------------------------------------------
describe('bindPercentInput — regression guard', () => {
  test('bindPercentInput is still defined', () => {
    expect(typeof globalThis.bindPercentInput).toBe('function');
  });

  test('bindPercentInput on-load: visible = raw * multiplier', () => {
    const { visible } = makeElements('pvis', 'praw', '0.0002');
    globalThis.bindPercentInput('pvis', 'praw', 100);
    expect(parseFloat(visible.value)).toBeCloseTo(0.02, 8);
  });

  test('bindPercentInput input event: raw = visible / multiplier', () => {
    const { visible, raw } = makeElements('pvis', 'praw', '0');
    globalThis.bindPercentInput('pvis', 'praw', 100);
    visible.value = '2';
    fireEvent(visible, 'input');
    expect(parseFloat(raw.value)).toBeCloseTo(0.02, 8);
  });
});
