import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";

import { App } from "./App.tsx";
import { initI18n } from "./i18n.ts";
import { SessionProvider } from "./session.tsx";
import "./styles.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // The API client already retries once after refreshing the token.
      // Retrying errors again here would only delay the message.
      retry: false,
      refetchOnWindowFocus: false,
    },
  },
});

await initI18n();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <SessionProvider>
          <App />
        </SessionProvider>
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
);
