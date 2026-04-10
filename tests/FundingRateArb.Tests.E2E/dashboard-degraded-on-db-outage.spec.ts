import { test, expect, request } from '@playwright/test';

const BASE_URL = process.env.E2E_AZURE_BASE_URL;
const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'admin@fundingratearb.com';
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD;
const DIAGNOSTICS_KEY = process.env.E2E_DIAGNOSTICS_KEY;

async function toggleDbOutage(apiCtx: Awaited<ReturnType<typeof request.newContext>>) {
  // TODO: endpoint POST /api/diagnostics/actions { "action": "toggle_db_outage" } does not exist yet.
  // Implement DiagnosticsController.toggle_db_outage before running this spec in production.
  // See documentation/runbooks/azure-stabilization-2026-04-09.md § Dependencies.
  return apiCtx.post('/api/diagnostics/actions', {
    data: { action: 'toggle_db_outage' },
    headers: { 'X-Diagnostics-Key': DIAGNOSTICS_KEY! },
  });
}

test('dashboard: degraded banner appears and no 500 when DB outage is simulated', async ({ page }) => {
  if (!BASE_URL || !ADMIN_PASSWORD || !DIAGNOSTICS_KEY) {
    test.skip(
      true,
      'E2E_AZURE_BASE_URL, E2E_ADMIN_PASSWORD, and E2E_DIAGNOSTICS_KEY are required'
    );
    return;
  }

  const apiCtx = await request.newContext({ baseURL: BASE_URL });

  // Step 1: Log in as admin (mirrors C# LoginAsync in ConnectivityTestPageTests.cs)
  await page.goto(`${BASE_URL}/Identity/Account/Login`);
  await page.fill("input[name='Input.Email']", ADMIN_EMAIL);
  await page.fill("input[name='Input.Password']", ADMIN_PASSWORD);
  await page.click("button[type='submit']");
  await page.waitForURL((url) => !url.pathname.includes('/Account/Login'), { timeout: 10_000 });

  // Step 2: Enable DB outage simulation
  const enableResponse = await toggleDbOutage(apiCtx);
  if (enableResponse.status() === 404) {
    test.skip(true, 'toggle_db_outage endpoint not yet implemented — skipping assertion');
    await apiCtx.dispose();
    return;
  }
  expect(enableResponse.ok(), `toggle_db_outage (enable) returned ${enableResponse.status()}`).toBeTruthy();

  try {
    // Step 3: Navigate to dashboard and assert degraded state
    // Degraded banner selector: div.alert.alert-warning (Views/Dashboard/Index.cshtml lines 33-45)
    // Banner is rendered when Model.DatabaseAvailable == false (DashboardController.DegradedDashboardView)
    const response = await page.goto(`${BASE_URL}/`);
    expect(
      response?.status() ?? 0,
      'Dashboard returned HTTP 500 during DB outage simulation'
    ).toBeLessThan(500);

    const degradedBanner = page.locator('div.alert.alert-warning');
    await expect(degradedBanner).toBeVisible({ timeout: 10_000 });
    await expect(degradedBanner).toContainText('Data source unavailable.');
  } finally {
    // Step 4: Restore — toggle DB outage off regardless of assertion outcome
    await toggleDbOutage(apiCtx);
    await apiCtx.dispose();
  }
});
