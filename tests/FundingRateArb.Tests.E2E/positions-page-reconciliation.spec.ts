import { test, expect, request } from '@playwright/test';

const BASE_URL = process.env.E2E_AZURE_BASE_URL;
const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'admin@fundingratearb.com';
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD;
const DIAGNOSTICS_KEY = process.env.E2E_DIAGNOSTICS_KEY;

test('positions: phantom position shows Failed status after reconciliation', async ({ page }) => {
  if (!BASE_URL || !ADMIN_PASSWORD || !DIAGNOSTICS_KEY) {
    test.skip(
      true,
      'E2E_AZURE_BASE_URL, E2E_ADMIN_PASSWORD, and E2E_DIAGNOSTICS_KEY are required'
    );
    return;
  }

  // Step 1: Log in as admin (mirrors C# LoginAsync in ConnectivityTestPageTests.cs)
  await page.goto(`${BASE_URL}/Identity/Account/Login`);
  await page.fill("input[name='Input.Email']", ADMIN_EMAIL);
  await page.fill("input[name='Input.Password']", ADMIN_PASSWORD);
  await page.click("button[type='submit']");
  await page.waitForURL((url) => !url.pathname.includes('/Account/Login'), { timeout: 10_000 });

  // Step 2: Seed a phantom position via the diagnostics endpoint
  // TODO: endpoint POST /api/diagnostics/actions { "action": "seed_phantom_position" } does not exist yet.
  // Implement DiagnosticsController.seed_phantom_position before running this spec in production.
  // See documentation/runbooks/azure-stabilization-2026-04-09.md § Dependencies.
  const apiCtx = await request.newContext({ baseURL: BASE_URL });
  const seedResponse = await apiCtx.post('/api/diagnostics/actions', {
    data: { action: 'seed_phantom_position' },
    headers: { 'X-Diagnostics-Key': DIAGNOSTICS_KEY },
  });
  if (seedResponse.status() === 404) {
    test.skip(true, 'seed_phantom_position endpoint not yet implemented — skipping assertion');
    await apiCtx.dispose();
    return;
  }
  expect(seedResponse.ok(), `seed_phantom_position returned ${seedResponse.status()}`).toBeTruthy();

  // Step 3: Navigate to /Positions and assert phantom row shows Failed
  await page.goto(`${BASE_URL}/Positions`);
  // CSS selector from Views/Positions/Index.cshtml
  const failedBadge = page.locator('span.badge.pos-status', { hasText: 'Failed' });
  await expect(failedBadge.first()).toBeVisible({ timeout: 15_000 });

  // TODO: Once seed_phantom_position is implemented, also assert ReconciliationDrift close reason.
  // The CloseReason enum may need a ReconciliationDrift value added — verify in Domain/Enums/PositionStatus.cs
  // and Domain/Enums/CloseReason.cs before updating this assertion.

  await apiCtx.dispose();
});
