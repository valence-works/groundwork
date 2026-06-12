import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true
  },
  server: {
    proxy: {
      "/healthz": "http://localhost:5097",
      "/tickets": "http://localhost:5097"
    }
  }
});
