import { useState, type FormEvent } from "react";
import { useMutation } from "@tanstack/react-query";
import type { Api, EvaluateResponse } from "../lib/api";
import { bannerFor, describeReason } from "../lib/markings";
import { MarkingBanner } from "../components/MarkingBanner";

const split = (s: string) => s.split(",").map((x) => x.trim()).filter(Boolean);

/** Lets a user see how a set of markings would apply to them, and validates markings before objects exist (increment 1.2). */
export function AccessCheckPage({ api }: { api: Api }) {
  const [form, setForm] = useState({
    exportJurisdiction: "Itar",
    eccn: "",
    usmlCategory: "XI(d)",
    cuiCategories: "EXPT",
    distributionStatement: "D",
    controllingOffice: "[Company] Engineering",
    distributionReason: "Critical technology",
    program: "ORCA",
    owningOrg: "ENG",
    exportAuthorizations: "",
    action: "Read",
  });
  const evaluate = useMutation({
    mutationFn: () =>
      api.post<EvaluateResponse>("/api/v1/access/evaluate", {
        action: form.action,
        markings: {
          exportJurisdiction: form.exportJurisdiction,
          distributionStatement: form.distributionStatement,
          owningOrg: form.owningOrg,
          eccn: form.eccn || null,
          usmlCategory: form.usmlCategory || null,
          cuiCategories: split(form.cuiCategories),
          controllingOffice: form.controllingOffice || null,
          distributionReason: form.distributionReason || null,
          program: form.program || null,
          exportAuthorizations: split(form.exportAuthorizations),
        },
      }),
  });
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    evaluate.mutate();
  };

  return (
    <section>
      <form className="grid-form" onSubmit={submit}>
        <label>
          Export jurisdiction
          <select value={form.exportJurisdiction} onChange={set("exportJurisdiction")}>
            <option value="None">None</option>
            <option value="Ear">EAR</option>
            <option value="Itar">ITAR</option>
          </select>
        </label>
        <label>
          ECCN (EAR)
          <input value={form.eccn} onChange={set("eccn")} placeholder="EAR99 or e.g. 9E610.a" />
        </label>
        <label>
          USML category (ITAR)
          <input value={form.usmlCategory} onChange={set("usmlCategory")} />
        </label>
        <label>
          CUI categories (comma-separated)
          <input value={form.cuiCategories} onChange={set("cuiCategories")} />
        </label>
        <label>
          Distribution statement
          <select value={form.distributionStatement} onChange={set("distributionStatement")}>
            {["A", "B", "C", "D", "E", "F"].map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </label>
        <label>
          Controlling office
          <input value={form.controllingOffice} onChange={set("controllingOffice")} />
        </label>
        <label>
          Distribution reason
          <input value={form.distributionReason} onChange={set("distributionReason")} />
        </label>
        <label>
          Program
          <input value={form.program} onChange={set("program")} />
        </label>
        <label>
          Owning organization
          <input value={form.owningOrg} onChange={set("owningOrg")} />
        </label>
        <label>
          Export authorizations (comma-separated)
          <input value={form.exportAuthorizations} onChange={set("exportAuthorizations")} placeholder="e.g. TAA-0042" />
        </label>
        <label>
          Action
          <select value={form.action} onChange={set("action")}>
            {["Read", "Download", "Modify", "Sign", "Administer"].map((a) => (
              <option key={a} value={a}>{a}</option>
            ))}
          </select>
        </label>
        <div className="actions">
          <button type="submit" disabled={evaluate.isPending}>Check access</button>
        </div>
      </form>

      {evaluate.isError && <p className="error" role="alert">{evaluate.error.message}</p>}
      {evaluate.data && (
        <div className="result" aria-live="polite">
          <MarkingBanner banner={bannerFor(evaluate.data.banner)} position="top" />
          <p className={evaluate.data.permitted ? "permit" : "deny"}>
            {evaluate.data.permitted ? "You would be permitted." : "You would be denied."}
          </p>
          {evaluate.data.reasons.length > 0 && (
            <ul>
              {evaluate.data.reasons.map((r) => (
                <li key={r}>{describeReason(r)}</li>
              ))}
            </ul>
          )}
        </div>
      )}
    </section>
  );
}
