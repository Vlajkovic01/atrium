import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// In development the web app runs on Vite's server and sends API calls to
// the Atrium server started with `dotnet run`. In production the Atrium
// server serves the built files itself, from the same origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5000",
    },
  },
  build: {
    outDir: "dist",
    sourcemap: true,
    // The server's content security policy forbids inline script and data:
    // scripts, so nothing may be inlined into the page.
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
  },
  test: {
    // Unit tests only. The files in e2e/ are Playwright tests and are run
    // by `pnpm run e2e` against a real server.
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
