import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './tests',
  testMatch: process.env.DATAFLOW_LIVE === '1' ? '**/*.live.ts' : '**/*.spec.ts',
  fullyParallel: true,
  use: { baseURL: 'http://localhost:5178', trace: 'retain-on-failure' },
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 5178 --strictPort',
    url: 'http://localhost:5178',
    reuseExistingServer: !process.env.CI,
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1440, height: 1000 } } },
    {
      name: 'mobile',
      use: { viewport: { width: 390, height: 844 }, colorScheme: 'dark', reducedMotion: 'reduce' },
    },
  ],
})
