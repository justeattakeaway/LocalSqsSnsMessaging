import { createRoot } from "react-dom/client";
import { App } from "@/components/app";
import "@/index.css";

createRoot(document.getElementById("app")!).render(<App />);
