import { useState, type FormEvent, type ReactNode } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import type { Api, Attributes, PartDetail, PartMetadata } from "../lib/api";
import { NoControlledData, type PageBanner } from "../lib/markings";
import { MarkingsFields, defaultItarMarkings, toMarkingsDto } from "../components/MarkingsFields";
import { AttributeFields } from "../components/AttributeFields";

export function CreatePartPage({ api, frame }: { api: Api; frame: (banner: PageBanner, body: ReactNode) => ReactNode }) {
  const meta = useQuery({ queryKey: ["part-metadata"], queryFn: () => api.get<PartMetadata>("/api/v1/metadata/parts") });
  const [form, setForm] = useState({ name: "", partType: "Component", defaultUnit: "EA", source: "Make", description: "" });
  const [attributes, setAttributes] = useState<Attributes>({});
  const [markings, setMarkings] = useState(defaultItarMarkings);
  const create = useMutation({
    mutationFn: () => api.post<PartDetail>("/api/v1/parts", { ...form, attributes, markings: toMarkingsDto(markings) }),
    onSuccess: (p) => {
      window.location.hash = `#/parts/${encodeURIComponent(p.number)}`;
    },
  });
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    create.mutate();
  };

  // A blank form shows no controlled data; the markings being entered are previewed on the created part.
  return frame(
    NoControlledData,
    meta.data ? (
      <form className="stack" onSubmit={submit}>
        <fieldset className="grid-form">
          <legend>Part</legend>
          <label>
            Name *
            <input required maxLength={200} value={form.name} onChange={set("name")} />
          </label>
          <label>
            Type
            <select value={form.partType} onChange={set("partType")}>
              {meta.data.partTypes.map((t) => (
                <option key={t} value={t}>{t}</option>
              ))}
            </select>
          </label>
          <label>
            Unit of measure
            <select value={form.defaultUnit} onChange={set("defaultUnit")}>
              {meta.data.units.map((u) => (
                <option key={u} value={u}>{u}</option>
              ))}
            </select>
          </label>
          <label>
            Source
            <select value={form.source} onChange={set("source")}>
              {meta.data.sources.map((s) => (
                <option key={s} value={s}>{s}</option>
              ))}
            </select>
          </label>
          <label className="wide">
            Description
            <textarea rows={3} maxLength={4000} value={form.description} onChange={set("description")} />
          </label>
        </fieldset>
        <fieldset className="grid-form">
          <legend>Attributes</legend>
          <AttributeFields defs={meta.data.attributes} value={attributes} onChange={setAttributes} />
        </fieldset>
        <fieldset className="grid-form">
          <legend>Security markings</legend>
          <MarkingsFields value={markings} onChange={setMarkings} />
        </fieldset>
        {create.isError && <p className="error" role="alert">{create.error.message}</p>}
        <div className="toolbar">
          <button type="submit" disabled={create.isPending}>Create part</button>
          <a className="button secondary" href="#/parts">Cancel</a>
        </div>
      </form>
    ) : meta.isError ? (
      <p className="error" role="alert">{meta.error.message}</p>
    ) : (
      <p>Loading…</p>
    ),
  );
}
