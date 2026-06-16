import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const backendUrl = process.env.GROUNDWORK_SUPPORT_TICKETS_API_URL ?? "http://localhost:5000";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true
  },
  server: {
    proxy: {
      "/healthz": backendUrl,
      "/modules": backendUrl,
      "/tickets": backendUrl
    }
  }
});
