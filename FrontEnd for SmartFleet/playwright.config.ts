import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests", use: { baseURL: "http://127.0.0.1:8461", channel: "msedge", headless: true },
  webServer: { command: "npm run dev", env: { PORT: "8461", FIGMA_DEV_SERVER_HOST: "127.0.0.1" },
    url: "http://127.0.0.1:8461", reuseExistingServer: false },
});
