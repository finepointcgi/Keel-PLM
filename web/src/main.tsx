import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ensureSignedIn } from "./lib/auth";
import { App } from "./App";
import "./styles.css";

const root = createRoot(document.getElementById("root")!);
const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });

ensureSignedIn()
  .then((user) => {
    if (!user) return; // redirecting to the identity provider
    root.render(
      <StrictMode>
        <QueryClientProvider client={queryClient}>
          <App user={user} />
        </QueryClientProvider>
      </StrictMode>,
    );
  })
  .catch((err: unknown) => {
    root.render(<p className="center error" role="alert">Sign-in failed: {String(err)}</p>);
  });
