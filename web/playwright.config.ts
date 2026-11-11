import { defineConfig, devices } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

// End-to-end tests run the real server, serving the built web app, against
// a fresh config directory and a fresh storage folder. Workers inherit
// these variables, so the paths are chosen once.
process.env.ATRIUM_E2E_ROOT ??= join(tmpdir(), "atrium-e2e", String(Date.now()));
const root = process.env.ATRIUM_E2E_ROOT;
const configDir = join(root, "config");
mkdirSync(configDir, { recursive: true });

const port = 5187;
const baseURL = `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: "e2e",
  // One server and one database: the tests tell a single story in order.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  reporter: process.env.CI ? [["list"], ["github"]] : "list",
  timeout: 60_000,
  // Creating an account hashes a password with Argon2id on purpose-built
  // slow settings; on a busy machine that alone can take several seconds.
  expect: { timeout: 15_000 },
  use: {
    baseURL,
    locale: "en-US",
    trace: "retain-on-failure",
    // Set ATRIUM_BROWSER_CHANNEL=msedge (or chrome) to use a browser that
    // is already installed, where Playwright's own cannot be downloaded.
    ...(process.env.ATRIUM_BROWSER_CHANNEL ? { channel: process.env.ATRIUM_BROWSER_CHANNEL } : {}),
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "dotnet run --project ../server/src/Atrium.Api --configuration Release --no-launch-profile",
    url: `${baseURL}/api/v1/health/ready`,
    timeout: 240_000,
    reuseExistingServer: false,
    stdout: "pipe",
    env: {
      ASPNETCORE_URLS: baseURL,
      ASPNETCORE_WEBROOT: resolve("dist"),
      ATRIUM_CONFIG_DIR: configDir,
      DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    },
  },
});
