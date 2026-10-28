import type { ReactNode } from "react";
import type { PageBanner } from "../lib/markings";
import { MarkingBanner } from "./MarkingBanner";

export interface NavItem {
  href: string;
  label: string;
  visible: boolean;
}

/** Every page renders through Layout, which requires a banner, so no page can show data unmarked. */
export function Layout(props: {
  banner: PageBanner;
  title: string;
  userName: string;
  nav: NavItem[];
  current: string;
  onSignOut: () => void;
  children: ReactNode;
}) {
  return (
    <div className="app">
      <MarkingBanner banner={props.banner} position="top" />
      <header className="topbar">
        <span className="brand">Keel PLM</span>
        <nav aria-label="Main">
          {props.nav
            .filter((n) => n.visible)
            .map((n) => (
              <a key={n.href} href={n.href} aria-current={props.current === n.href ? "page" : undefined}>
                {n.label}
              </a>
            ))}
        </nav>
        <span className="user">
          {props.userName}
          <button type="button" className="link" onClick={props.onSignOut}>
            Sign out
          </button>
        </span>
      </header>
      <main>
        <h1>{props.title}</h1>
        {props.children}
      </main>
      <MarkingBanner banner={props.banner} position="bottom" />
    </div>
  );
}
