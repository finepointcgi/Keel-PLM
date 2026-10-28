import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import type { User } from "oidc-client-ts";
import { createApi, type Me } from "./lib/api";
import { accessToken, signOut } from "./lib/auth";
import { bannerFor, NoControlledData, type PageBanner } from "./lib/markings";
import { Layout } from "./components/Layout";
import { ProfilePage } from "./pages/ProfilePage";
import { AccessCheckPage } from "./pages/AccessCheckPage";
import { AuditPage } from "./pages/AuditPage";
import { PartsSearchPage } from "./pages/PartsSearchPage";
import { CreatePartPage } from "./pages/CreatePartPage";
import { PartDetailPage } from "./pages/PartDetailPage";

function useHash() {
  const [hash, setHash] = useState(window.location.hash || "#/parts");
  useEffect(() => {
    const onChange = () => setHash(window.location.hash || "#/parts");
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return hash;
}

// ASSUMPTION(A20): audit records reveal who accessed export-controlled data, so the audit view is marked CUI.
const AuditBanner: PageBanner = bannerFor("CUI");

export function App({ user }: { user: User }) {
  const api = useMemo(() => createApi(accessToken), []);
  const hash = useHash();
  const me = useQuery({ queryKey: ["me", user.profile.sub], queryFn: () => api.get<Me>("/api/v1/me") });

  if (me.isPending) return <p className="center">Loading your profile…</p>;
  if (me.isError) return <p className="center error" role="alert">{me.error.message}</p>;

  const isAuditor = me.data.roles.includes("plm-auditor");
  const canCreate = me.data.roles.includes("plm-author") || me.data.roles.includes("plm-admin");
  const nav = [
    { href: "#/parts", label: "Parts", visible: true },
    { href: "#/access", label: "Access check", visible: true },
    { href: "#/audit", label: "Audit log", visible: isAuditor },
    { href: "#/profile", label: "My access", visible: true },
  ];
  const [path, search = ""] = hash.split("?");
  const section = nav.find((n) => path === n.href || path!.startsWith(n.href + "/"))?.href ?? "#/parts";

  // Pages compute their own banner from the data they display and render through this frame (ADR-0010).
  const frame = (title: string) => (banner: PageBanner, body: ReactNode) => (
    <Layout banner={banner} title={title} userName={me.data.displayName} nav={nav} current={section} onSignOut={() => void signOut()}>
      {body}
    </Layout>
  );

  const partMatch = /^#\/parts\/(.+)$/.exec(path!);
  if (path === "#/parts/new") return <CreatePartPage api={api} frame={frame("New part")} />;
  if (partMatch) {
    const number = decodeURIComponent(partMatch[1]!);
    return <PartDetailPage key={hash} api={api} frame={frame("Part")} number={number} revision={new URLSearchParams(search).get("revision")} />;
  }
  if (path === "#/audit" && isAuditor) return <>{frame("Audit log")(AuditBanner, <AuditPage api={api} />)}</>;
  if (path === "#/access") return <>{frame("Access check")(NoControlledData, <AccessCheckPage api={api} />)}</>;
  if (path === "#/profile") return <>{frame("My access")(NoControlledData, <ProfilePage me={me.data} />)}</>;
  return <PartsSearchPage api={api} frame={frame("Parts")} canCreate={canCreate} />;
}
