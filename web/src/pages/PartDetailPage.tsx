import { useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, type Api, type Attributes, type PartDetail, type PartMetadata } from "../lib/api";
import { bannerFor, describeReason, NoControlledData, type PageBanner } from "../lib/markings";
import { AttributeFields, formatAttribute } from "../components/AttributeFields";
import { MarkingsFields, fromMarkings, toMarkingsDto, type MarkingsInput } from "../components/MarkingsFields";

type Frame = (banner: PageBanner, body: ReactNode) => ReactNode;

export function PartDetailPage({ api, frame, number, revision }: { api: Api; frame: Frame; number: string; revision: string | null }) {
  const qc = useQueryClient();
  const key = ["part", number, revision];
  const part = useQuery({
    queryKey: key,
    queryFn: () => api.get<PartDetail>(`/api/v1/parts/${encodeURIComponent(number)}${revision ? `?revision=${revision}` : ""}`),
  });
  const meta = useQuery({ queryKey: ["part-metadata"], queryFn: () => api.get<PartMetadata>("/api/v1/metadata/parts") });
  const act = useMutation({
    mutationFn: ({ method, path, body }: { method: "post" | "put"; path: string; body?: unknown }) =>
      api[method]<PartDetail>(`/api/v1/parts/${encodeURIComponent(number)}${path}`, body),
    onSuccess: (p) => {
      qc.setQueryData(key, p);
      void qc.invalidateQueries({ queryKey: ["parts"] });
    },
  });

  // Check-in saves the open working copy first so unsaved edits are never silently dropped.
  const checkIn = useMutation({
    mutationFn: async ({ body, comment }: { body: { description: string; attributes: Attributes }; comment: string }) => {
      const path = `/api/v1/parts/${encodeURIComponent(number)}`;
      await api.put<PartDetail>(`${path}/working-copy`, body);
      return api.post<PartDetail>(`${path}/checkin`, { comment });
    },
    onSuccess: (p) => {
      qc.setQueryData(key, p);
      void qc.invalidateQueries({ queryKey: ["parts"] });
    },
  });

  if (part.isError) {
    const notFound = part.error instanceof ApiError && part.error.status === 404;
    return frame(NoControlledData, <p className="error" role="alert">{notFound ? `Part ${number} was not found, or you are not authorized to see it.` : part.error.message}</p>);
  }
  if (!part.data || !meta.data) return frame(NoControlledData, <p>Loading…</p>);

  const p = part.data;
  const defs = meta.data.attributes;
  const latest = p.revision.isLatest;
  const co = p.checkout;
  const run = (method: "post" | "put", path: string, body?: unknown) => act.mutate({ method, path, body });

  return frame(
    bannerFor(p.banner),
    <article className="stack">
      <header className="part-header">
        <div>
          <h2>
            {p.number} · {p.name}
          </h2>
          <p className="muted">
            {p.partType} · {p.source} · unit {p.defaultUnit} · created by {p.createdBy}
          </p>
        </div>
        <div className="badges">
          <span className="badge">Version {p.iteration.label}</span>
          <span className={`badge state-${p.revision.state.toLowerCase()}`}>{p.revision.stateLabel}</span>
          {p.revision.isLocked && <span className="badge">Locked</span>}
        </div>
      </header>

      {p.revisions.length > 1 && (
        <nav className="toolbar" aria-label="Revisions">
          {p.revisions.map((r) => (
            <a key={r.id} href={`#/parts/${encodeURIComponent(p.number)}${r.isLatest ? "" : `?revision=${r.revision}`}`} aria-current={r.id === p.revision.id ? "page" : undefined}>
              Rev {r.revision} ({r.stateLabel})
            </a>
          ))}
        </nav>
      )}

      {act.isError && <p className="error" role="alert">{act.error.message}</p>}
      {checkIn.isError && <p className="error" role="alert">{checkIn.error.message}</p>}
      {!p.permissions.canModify && (
        <p className="muted">Read only: {p.permissions.modifyDeniedReasons.map(describeReason).join(" ")}</p>
      )}

      {latest && (
        <section className="panel">
          <h3>Work</h3>
          {co && !co.isMine && <p>Checked out by {co.userName ?? co.userId} since {fmt(co.checkedOutAt)}.</p>}
          <div className="toolbar">
            {!co && p.permissions.canModify && !p.revision.isLocked && (
              <button type="button" onClick={() => run("post", "/checkout")} disabled={act.isPending}>Check out</button>
            )}
            {co && (co.isMine || p.permissions.canAdminister) && (
              <button type="button" className="secondary" onClick={() => run("post", "/undo-checkout")} disabled={act.isPending}>Undo check-out</button>
            )}
            {p.transitions.map((t) => (
              <button key={t.to} type="button" className="secondary" disabled={!t.allowed || act.isPending} title={t.reason ?? undefined}
                onClick={() => run("post", "/state", { toState: t.to })}>
                Move to {t.label}
              </button>
            ))}
          </div>
          {p.transitions.filter((t) => !t.allowed && t.reason).map((t) => (
            <p key={t.to} className="muted small">{t.label}: {t.reason}</p>
          ))}
          {co?.isMine && <WorkingCopyEditor key={co.checkedOutAt} defs={defs} detail={p} busy={act.isPending || checkIn.isPending} onSave={(body) => run("put", "/working-copy", body)} onCheckIn={(body, comment) => checkIn.mutate({ body, comment })} />}
          {p.permissions.canAdminister && <SetStateOverride states={meta.data.lifecycle.states} current={p.revision.state} busy={act.isPending} onSubmit={(toState, reason) => run("post", "/state", { toState, reason, override: true })} />}
        </section>
      )}

      <section className="panel">
        <h3>Iteration {p.iteration.label}</h3>
        <p>{p.iteration.description || <span className="muted">No description</span>}</p>
        <dl className="facts">
          {Object.entries(p.iteration.attributes).map(([k, v]) => {
            const [label, text] = formatAttribute(defs, k, v);
            return (
              <div key={k} className="fact-row">
                <dt>{label}</dt>
                <dd>{text}</dd>
              </div>
            );
          })}
        </dl>
      </section>

      <MarkingsPanel detail={p} busy={act.isPending} onSubmit={(markings, reason) => run("put", "/markings", { markings, reason })} />

      <section className="panel">
        <h3>History</h3>
        <table className="data">
          <thead>
            <tr>
              <th scope="col">Version</th>
              <th scope="col">Checked in (UTC)</th>
              <th scope="col">By</th>
              <th scope="col">Comment</th>
            </tr>
          </thead>
          <tbody>
            {p.history.map((h) => (
              <tr key={h.id}>
                <td>{h.label}</td>
                <td>{fmt(h.createdAt)}</td>
                <td>{h.createdBy}</td>
                <td>{h.comment}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </article>,
  );
}

const fmt = (iso: string) => iso.replace("T", " ").slice(0, 16);

function WorkingCopyEditor({ defs, detail, busy, onSave, onCheckIn }: {
  defs: PartMetadata["attributes"]; detail: PartDetail; busy: boolean;
  onSave: (body: { description: string; attributes: Attributes }) => void;
  onCheckIn: (body: { description: string; attributes: Attributes }, comment: string) => void;
}) {
  const [description, setDescription] = useState(detail.checkout?.description ?? "");
  const [attributes, setAttributes] = useState<Attributes>(detail.checkout?.attributes ?? {});
  const [comment, setComment] = useState("");
  return (
    <div className="stack">
      <p className="muted">You have this part checked out. Changes stay private until you check in.</p>
      <div className="grid-form">
        <label className="wide">
          Description
          <textarea rows={3} maxLength={4000} value={description} onChange={(e) => setDescription(e.target.value)} />
        </label>
        <AttributeFields defs={defs} value={attributes} onChange={setAttributes} />
      </div>
      <div className="toolbar">
        <button type="button" className="secondary" disabled={busy} onClick={() => onSave({ description, attributes })}>Save working copy</button>
        <input aria-label="Check-in comment" placeholder="Check-in comment" value={comment} onChange={(e) => setComment(e.target.value)} className="grow" />
        <button type="button" disabled={busy} onClick={() => onCheckIn({ description, attributes }, comment)}>Check in</button>
      </div>
    </div>
  );
}

function SetStateOverride({ states, current, busy, onSubmit }: {
  states: { state: string; label: string }[]; current: string; busy: boolean; onSubmit: (state: string, reason: string) => void;
}) {
  const [state, setState] = useState(current);
  const [reason, setReason] = useState("");
  return (
    <details className="admin">
      <summary>Administrator: set state</summary>
      <div className="toolbar">
        <select aria-label="Target state" value={state} onChange={(e) => setState(e.target.value)}>
          {states.map((s) => (
            <option key={s.state} value={s.state}>{s.label}</option>
          ))}
        </select>
        <input aria-label="Override reason" placeholder="Reason (required, audited)" value={reason} onChange={(e) => setReason(e.target.value)} className="grow" />
        <button type="button" disabled={busy || state === current} onClick={() => onSubmit(state, reason)}>Set state</button>
      </div>
    </details>
  );
}

function MarkingsPanel({ detail, busy, onSubmit }: { detail: PartDetail; busy: boolean; onSubmit: (m: ReturnType<typeof toMarkingsDto>, reason: string) => void }) {
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState<MarkingsInput>(fromMarkings(detail.markings));
  const [reason, setReason] = useState("");
  const m = detail.markings;
  const canEdit = detail.permissions.canModify && detail.revision.isLatest && !detail.revision.isLocked;
  return (
    <section className="panel">
      <h3>Security markings</h3>
      {!editing ? (
        <>
          <dl className="facts">
            <dt>Export jurisdiction</dt>
            <dd>{m.exportJurisdiction === "Itar" ? `ITAR, USML ${m.usmlCategory}` : m.exportJurisdiction === "Ear" ? `EAR, ECCN ${m.eccn}` : "None"}</dd>
            <dt>CUI categories</dt>
            <dd>{m.cuiCategories.join(", ") || "None"}</dd>
            <dt>Distribution</dt>
            <dd>Statement {m.distributionStatement}{m.controllingOffice ? `: ${m.distributionReason}; controlling office ${m.controllingOffice}` : ""}</dd>
            <dt>Program</dt>
            <dd>{m.program ?? "None"}</dd>
            <dt>Owning organization</dt>
            <dd>{m.owningOrg}</dd>
            <dt>Export authorizations</dt>
            <dd>{m.exportAuthorizations.join(", ") || "None"}</dd>
          </dl>
          {canEdit && (
            <button type="button" className="secondary" onClick={() => { setValue(fromMarkings(m)); setEditing(true); }}>Change markings</button>
          )}
        </>
      ) : (
        <div className="stack">
          <div className="grid-form">
            <MarkingsFields value={value} onChange={setValue} />
            <label className="wide">
              Reason for change (required, audited)
              <input value={reason} onChange={(e) => setReason(e.target.value)} />
            </label>
          </div>
          <div className="toolbar">
            <button type="button" disabled={busy} onClick={() => { onSubmit(toMarkingsDto(value), reason); setEditing(false); }}>Save markings</button>
            <button type="button" className="secondary" onClick={() => setEditing(false)}>Cancel</button>
          </div>
        </div>
      )}
    </section>
  );
}
