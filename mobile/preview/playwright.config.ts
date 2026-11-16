import { defineConfig } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

// Shows the mobile app's screens in a browser at a phone's size, against
// a real Atrium server, and saves a picture of each:
//   pnpm --filter @atrium/mobile run preview
// React Native's components are drawn by react-native-web, so layout,
// texts, colours and the flow between screens are real; the camera, the
// Keystore and anything else that exists only on a phone are not. It is a
// way to look at the app without a phone, not a test of the phone app.
// Uses the Edge already installed, so nothing is downloaded. Not in CI.
process.env.ATRIUM_E2E_ROOT ??= join(tmpdir(), "atrium-mobile-preview", String(Date.now()));
const configDir = join(process.env.ATRIUM_E2E_ROOT, "config");
mkdirSync(configDir, { recursive: true });

const server = "http://127.0.0.1:5192";
const app = "http://127.0.0.1:5193";

export default defineConfig({
  testDir: ".",
  testMatch: "*.shots.ts",
  workers: 1,
  reporter: "list",
  timeout: 180_000,
  expect: { timeout: 20_000 },
  use: {
    baseURL: app,
    channel: process.env.ATRIUM_BROWSER_CHANNEL ?? "msedge",
    viewport: { width: 412, height: 915 },
    deviceScaleFactor: 2,
    hasTouch: true,
  },
  webServer: [
    {
      command: "dotnet run --project ../server/src/Atrium.Api --configuration Release --no-launch-profile",
      cwd: resolve("."),
      url: `${server}/api/v1/health/ready`,
      timeout: 300_000,
      reuseExistingServer: false,
      env: { ASPNETCORE_URLS: server, ATRIUM_CONFIG_DIR: configDir, DOTNET_CLI_TELEMETRY_OPTOUT: "1" },
    },
    {
      command: `node preview/serve.mjs dist-web 5193 ${server}`,
      cwd: resolve("."),
      url: app,
      timeout: 30_000,
      reuseExistingServer: false,
    },
  ],
});
