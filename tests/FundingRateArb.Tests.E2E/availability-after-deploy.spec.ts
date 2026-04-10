import { test, expect, request } from '@playwright/test';

const BASE_URL = process.env.E2E_AZURE_BASE_URL;
const PROBE_INTERVAL_MS = 60_000;
const PROBE_COUNT = 15;
const MAX_RESPONSE_MS = 30_000;

test('availability: probe / and /healthz every 60s for 15 minutes', async () => {
  if (!BASE_URL) {
    test.skip(true, 'E2E_AZURE_BASE_URL not set — skipping post-deploy availability check');
    return;
  }
  test.setTimeout((PROBE_COUNT + 1) * PROBE_INTERVAL_MS);

  const ctx = await request.newContext({ baseURL: BASE_URL });

  try {
    for (let i = 1; i <= PROBE_COUNT; i++) {
      for (const path of ['/', '/healthz']) {
        const start = Date.now();
        const response = await ctx.get(path);
        const elapsed = Date.now() - start;

        expect(
          response.status(),
          `Iteration ${i}, ${path}: expected 2xx, got ${response.status()} after ${elapsed}ms`
        ).toBeGreaterThanOrEqual(200);
        expect(
          response.status(),
          `Iteration ${i}, ${path}: expected 2xx, got ${response.status()} after ${elapsed}ms`
        ).toBeLessThan(300);

        expect(
          elapsed,
          `Iteration ${i}, ${path}: response time ${elapsed}ms exceeded ${MAX_RESPONSE_MS}ms`
        ).toBeLessThan(MAX_RESPONSE_MS);
      }

      if (i < PROBE_COUNT) {
        await new Promise((resolve) => setTimeout(resolve, PROBE_INTERVAL_MS));
      }
    }
  } finally {
    await ctx.dispose();
  }
});
