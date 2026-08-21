import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  use: { baseURL: process.env.CASEMESH_WEB_ORIGIN ?? "http://127.0.0.1:3000", trace: "retain-on-failure" },
  reporter: "list"
});
