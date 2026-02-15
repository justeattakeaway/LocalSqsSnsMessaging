import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "path";
import fs from "fs";

function dashboardOutputPlugin() {
  return {
    name: "dashboard-output",
    transformIndexHtml: {
      order: "post" as const,
      handler(html: string) {
        return html
          .replace(/\/dashboard\.js/g, "/_ui/dashboard.js")
          .replace(/\/dashboard\.css/g, "/_ui/dashboard.css");
      },
    },
    closeBundle() {
      const outDir = path.resolve(__dirname, "../wwwroot");
      const src = path.join(outDir, "index.html");
      const dst = path.join(outDir, "dashboard.html");
      if (fs.existsSync(src)) {
        fs.renameSync(src, dst);
      }
    },
  };
}

export default defineConfig({
  plugins: [
    react({
      babel: {
        plugins: [["babel-plugin-react-compiler"]],
      },
    }),
    tailwindcss(),
    dashboardOutputPlugin(),
  ],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true,
    cssCodeSplit: false,
    rollupOptions: {
      output: {
        entryFileNames: "dashboard.js",
        assetFileNames: (assetInfo) => {
          if (assetInfo.names?.some((n) => n.endsWith(".css")))
            return "dashboard.css";
          return "[name][extname]";
        },
        inlineDynamicImports: true,
      },
    },
  },
  server: {
    proxy: {
      "/_ui/api": "http://localhost:5050",
    },
  },
});
