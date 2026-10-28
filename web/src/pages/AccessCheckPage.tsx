import { useState, type FormEvent } from "react";
import { useMutation } from "@tanstack/react-query";
import type { Api, EvaluateResponse } from "../lib/api";
import { bannerFor, describeReason } from "../lib/markings";
import { MarkingBanner } from "../components/MarkingBanner";
import { MarkingsFields, defaultItarMarkings, toMarkingsDto } from "../components/MarkingsFields";

/** Lets a user see how a set of markings would apply to them before any object carries them. */
export function AccessCheckPage({ api }: { api: Api }) {
  const [markings, setMarkings] = useState(defaultItarMarkings);
  const [action, setAction] = useState("Read");
  const evaluate = useMutation({
    mutationFn: () => api.post<EvaluateResponse>("/api/v1/access/evaluate", { action, markings: toMarkingsDto(markings) }),
  });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    evaluate.mutate();
  };

  return (
    <section>
      <form className="grid-form" onSubmit={submit}>
        <MarkingsFields value={markings} onChange={setMarkings} />
        <label>
          Action
          <select value={action} onChange={(e) => setAction(e.target.value)}>
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
