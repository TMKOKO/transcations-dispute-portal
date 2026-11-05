import { defineConfig, devices } from '@playwright/test'

// Runs against the real stack that scripts/e2e.sh starts. Start from there, not from here: these
// tests write disputes, and the script keeps them away from the demo's data.
export default defineConfig({
  testDir: './e2e',
  // One shared stack: decisions and the two-account tests are easier to follow one at a time.
  workers: 1,
  timeout: 90_000,
  outputDir: './test-results',
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:3000',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
