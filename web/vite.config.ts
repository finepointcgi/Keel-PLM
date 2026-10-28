import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// Dev server proxies the API so the SPA and API are same-origin, as in production (ADR-0007).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: { "/api": "http://localhost:5080", "/health": "http://localhost:5080" },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["src/test-setup.ts"],
  },
});
