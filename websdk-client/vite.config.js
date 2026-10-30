import { defineConfig } from "vite";

export default defineConfig({
  // Read VITE_* variables from the .env file at the repository root.
  envDir: "..",
  server: {
    port: 6001,
    strictPort: true,
  },
  preview: {
    port: 6001,
    strictPort: true,
  },
});
