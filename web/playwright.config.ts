import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  // Browsers treat localhost as a trustworthy loopback origin for Secure cookies.
  // Keep the BFF session cookie secure in E2E instead of weakening test-only cookie policy.
  use: { baseURL: process.env.CASEMESH_WEB_ORIGIN ?? "http://localhost:3000", trace: "retain-on-failure" },
  reporter: "list"
});
