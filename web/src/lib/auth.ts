import { InMemoryWebStorage, UserManager, WebStorageStateStore, type User } from "oidc-client-ts";

export interface ClientConfig {
  authority: string;
  clientId: string;
  audience: string;
}

let manager: UserManager | undefined;

/** Builds the OIDC client from /api/v1/config so no IdP coordinates are baked into the bundle. */
export async function getUserManager(): Promise<UserManager> {
  if (manager) return manager;
  const res = await fetch("/api/v1/config");
  if (!res.ok) throw new Error(`Could not load client configuration (${res.status})`);
  const config = (await res.json()) as ClientConfig;
  manager = new UserManager({
    authority: config.authority,
    client_id: config.clientId,
    redirect_uri: `${window.location.origin}/`,
    post_logout_redirect_uri: `${window.location.origin}/`,
    response_type: "code",
    scope: "openid profile",
    // ADR-0007: tokens live in memory only, never in localStorage/sessionStorage.
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    automaticSilentRenew: false,
  });
  return manager;
}

/** Completes a redirect callback if present, otherwise starts sign-in when there is no user. */
export async function ensureSignedIn(): Promise<User | null> {
  const um = await getUserManager();
  const params = new URLSearchParams(window.location.search);
  if (params.has("code") && params.has("state")) {
    const user = await um.signinCallback();
    // Restore the page the user asked for before the IdP redirect (state carries the original hash).
    const hash = typeof user?.state === "string" && user.state.startsWith("#/") ? user.state : window.location.hash;
    window.history.replaceState({}, document.title, window.location.pathname + hash);
    return user ?? null;
  }
  const user = await um.getUser();
  if (user && !user.expired) return user;
  await um.signinRedirect({ state: window.location.hash });
  return null;
}

export async function signOut(): Promise<void> {
  const um = await getUserManager();
  await um.signoutRedirect();
}
