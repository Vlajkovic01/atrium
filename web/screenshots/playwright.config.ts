import { defineConfig } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

// Takes screenshots of the real app for documentation and review:
//   pnpm --filter @atrium/web run build
//   pnpm --filter @atrium/web run screenshots
// Uses the Edge already installed on the machine, so nothing is downloaded.
// Not part of CI; the end-to-end tests in ../e2e are.
process.env.ATRIUM_E2E_ROOT ??= join(tmpdir(), "atrium-screenshots", String(Date.now()));
const configDir = join(process.env.ATRIUM_E2E_ROOT, "config");
mkdirSync(configDir, { recursive: true });

const baseURL = "http://127.0.0.1:5188";

export default defineConfig({
  testDir: ".",
  testMatch: "*.shots.ts",
  workers: 1,
  reporter: "list",
  timeout: 120_000,
  use: {
    baseURL,
    channel: process.env.ATRIUM_BROWSER_CHANNEL ?? "msedge",
    locale: "en-US",
    viewport: { width: 1280, height: 860 },
    deviceScaleFactor: 2,
  },
  webServer: {
    command: "dotnet run --project ../server/src/Atrium.Api --configuration Release --no-launch-profile",
    // Paths are relative to the web app, not to this config file's folder.
    cwd: resolve("."),
    url: `${baseURL}/api/v1/health/ready`,
    timeout: 300_000,
    reuseExistingServer: false,
    env: {
      ASPNETCORE_URLS: baseURL,
      ASPNETCORE_WEBROOT: resolve("dist"),
      ATRIUM_CONFIG_DIR: configDir,
      DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    },
  },
});
