import { useQuery, useMutation } from "@tanstack/react-query";
import type { Api, AuditEvent, VerificationResult } from "../lib/api";

export function AuditPage({ api }: { api: Api }) {
  const events = useQuery({
    queryKey: ["audit-events"],
    queryFn: () => api.get<AuditEvent[]>("/api/v1/audit/events?limit=200"),
  });
  const verify = useMutation({ mutationFn: () => api.post<VerificationResult>("/api/v1/audit/verify") });

  return (
    <section>
      <div className="toolbar">
        <button type="button" onClick={() => verify.mutate()} disabled={verify.isPending}>Verify chain</button>
        <button type="button" className="secondary" onClick={() => void events.refetch()}>Refresh</button>
      </div>
      {verify.data && (
        <div className={verify.data.valid ? "permit" : "deny"} role="status">
          {verify.data.valid
            ? `Chain valid: ${verify.data.eventsChecked} events, ${verify.data.checkpointsChecked} signed checkpoints.`
            : `Chain INVALID: ${verify.data.failures.join("; ")}`}
        </div>
      )}
      {verify.isError && <p className="error" role="alert">{verify.error.message}</p>}
      {events.isError && <p className="error" role="alert">{events.error.message}</p>}
      {events.data && (
        <table className="data">
          <thead>
            <tr>
              <th scope="col">Seq</th>
              <th scope="col">Time (UTC)</th>
              <th scope="col">Actor</th>
              <th scope="col">Action</th>
              <th scope="col">Object</th>
              <th scope="col">Outcome</th>
              <th scope="col">Hash</th>
            </tr>
          </thead>
          <tbody>
            {events.data.map((e) => (
              <tr key={e.seq}>
                <td>{e.seq}</td>
                <td>{e.occurredAt.replace("T", " ").slice(0, 19)}</td>
                <td title={e.actor}>{e.actorName ?? e.actor}</td>
                <td>{e.action}</td>
                <td>{[e.objectType, e.objectId].filter(Boolean).join(" ")}</td>
                <td className={`outcome-${e.outcome.toLowerCase()}`}>{e.outcome}</td>
                <td><code title={e.hash}>{e.hash.slice(0, 12)}</code></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
