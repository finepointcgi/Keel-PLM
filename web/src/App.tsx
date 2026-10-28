import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import type { User } from "oidc-client-ts";
import { createApi, type Me } from "./lib/api";
import { signOut } from "./lib/auth";
import { bannerFor, NoControlledData, type PageBanner } from "./lib/markings";
import { Layout } from "./components/Layout";
import { ProfilePage } from "./pages/ProfilePage";
import { AccessCheckPage } from "./pages/AccessCheckPage";
import { AuditPage } from "./pages/AuditPage";

function useHash() {
  const [hash, setHash] = useState(window.location.hash || "#/profile");
  useEffect(() => {
    const onChange = () => setHash(window.location.hash || "#/profile");
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return hash;
}

// ASSUMPTION(A20): audit records reveal who accessed export-controlled data, so the audit view is marked CUI.
const AuditBanner: PageBanner = bannerFor("CUI");

export function App({ user }: { user: User }) {
  const api = useMemo(() => createApi(() => user.access_token), [user]);
  const hash = useHash();
  const me = useQuery({ queryKey: ["me"], queryFn: () => api.get<Me>("/api/v1/me") });

  if (me.isPending) return <p className="center">Loading your profile…</p>;
  if (me.isError) return <p className="center error" role="alert">{me.error.message}</p>;

  const isAuditor = me.data.roles.includes("plm-auditor");
  const nav = [
    { href: "#/profile", label: "My access", visible: true },
    { href: "#/access", label: "Access check", visible: true },
    { href: "#/audit", label: "Audit log", visible: isAuditor },
  ];
  const page =
    hash === "#/audit" && isAuditor
      ? { title: "Audit log", banner: AuditBanner, body: <AuditPage api={api} /> }
      : hash === "#/access"
        ? { title: "Access check", banner: NoControlledData, body: <AccessCheckPage api={api} /> }
        : { title: "My access", banner: NoControlledData, body: <ProfilePage me={me.data} /> };

  return (
    <Layout banner={page.banner} title={page.title} userName={me.data.displayName} nav={nav}
      current={nav.some((n) => n.href === hash) ? hash : "#/profile"} onSignOut={() => void signOut()}>
      {page.body}
    </Layout>
  );
}
