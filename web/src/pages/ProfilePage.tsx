import type { Me } from "../lib/api";

function List({ items }: { items: string[] }) {
  return items.length ? <>{items.join(", ")}</> : <span className="muted">None</span>;
}

export function ProfilePage({ me }: { me: Me }) {
  return (
    <section aria-labelledby="profile-heading">
      <p className="muted" id="profile-heading">
        These attributes come from your identity provider and decide which controlled data you can see. Contact your security office if they are wrong.
      </p>
      <dl className="facts">
        <dt>Name</dt>
        <dd>{me.displayName}</dd>
        <dt>U.S. person</dt>
        <dd>{me.usPerson ? "Yes" : "No"}</dd>
        <dt>Citizenship</dt>
        <dd>{me.citizenship ?? <span className="muted">Not provided</span>}</dd>
        <dt>Organization</dt>
        <dd>{me.org ?? <span className="muted">Not provided</span>}</dd>
        <dt>Programs</dt>
        <dd><List items={me.programs} /></dd>
        <dt>Export authorizations</dt>
        <dd><List items={me.exportAuthorizations} /></dd>
        <dt>CUI categories</dt>
        <dd><List items={me.cuiCategories} /></dd>
        <dt>Roles</dt>
        <dd><List items={me.roles} /></dd>
      </dl>
    </section>
  );
}
