import { useState, type FormEvent, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import type { Api, PartSearchResult } from "../lib/api";
import { bannerFor, NoControlledData, type PageBanner } from "../lib/markings";
import { MarkingChip } from "../components/MarkingsFields";

const PAGE = 50;

export function PartsSearchPage({ api, frame, canCreate }: { api: Api; frame: (banner: PageBanner, body: ReactNode) => ReactNode; canCreate: boolean }) {
  const [input, setInput] = useState("");
  const [query, setQuery] = useState({ q: "", state: "", offset: 0 });
  const result = useQuery({
    queryKey: ["parts", query],
    queryFn: () =>
      api.get<PartSearchResult>(`/api/v1/parts?q=${encodeURIComponent(query.q)}&limit=${PAGE}&offset=${query.offset}${query.state ? `&state=${query.state}` : ""}`),
  });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    setQuery({ ...query, q: input, offset: 0 });
  };

  return frame(
    result.data ? bannerFor(result.data.pageBanner) : NoControlledData,
    <section>
      <form className="toolbar" onSubmit={submit} role="search">
        <input aria-label="Search parts" placeholder="Number or name" value={input} onChange={(e) => setInput(e.target.value)} className="grow" />
        <select aria-label="State" value={query.state} onChange={(e) => setQuery({ ...query, state: e.target.value, offset: 0 })}>
          <option value="">Any state</option>
          <option value="InWork">In Work</option>
          <option value="UnderReview">Under Review</option>
          <option value="Released">Released</option>
          <option value="Obsolete">Obsolete</option>
        </select>
        <button type="submit">Search</button>
        {canCreate && <a className="button secondary" href="#/parts/new">New part</a>}
      </form>
      {result.isError && <p className="error" role="alert">{result.error.message}</p>}
      {result.data && (
        <>
          <p className="muted">
            {result.data.total} part{result.data.total === 1 ? "" : "s"} you can access
          </p>
          <table className="data">
            <thead>
              <tr>
                <th scope="col">Number</th>
                <th scope="col">Name</th>
                <th scope="col">Type</th>
                <th scope="col">Version</th>
                <th scope="col">State</th>
                <th scope="col">Markings</th>
              </tr>
            </thead>
            <tbody>
              {result.data.items.map((p) => (
                <tr key={p.id}>
                  <td><a href={`#/parts/${encodeURIComponent(p.number)}`}>{p.number}</a></td>
                  <td>{p.name}</td>
                  <td>{p.partType}</td>
                  <td>{p.revision}.{p.iteration}</td>
                  <td>{p.state}</td>
                  <td><MarkingChip text={p.banner} /></td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="toolbar">
            <button type="button" className="secondary" disabled={query.offset === 0} onClick={() => setQuery({ ...query, offset: Math.max(0, query.offset - PAGE) })}>Previous</button>
            <button type="button" className="secondary" disabled={query.offset + PAGE >= result.data.total} onClick={() => setQuery({ ...query, offset: query.offset + PAGE })}>Next</button>
          </div>
        </>
      )}
    </section>,
  );
}
