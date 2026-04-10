import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  workers: 1,
  timeout: 16 * 60 * 1000, // 16 min — covers the 15-min availability polling window
  use: {
    baseURL: process.env.E2E_AZURE_BASE_URL,
    navigationTimeout: 35_000, // covers the 30s spec threshold with margin
  },
});
