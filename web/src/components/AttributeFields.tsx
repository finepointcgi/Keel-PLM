import type { AttributeDefinition, Attributes } from "../lib/api";

/** Renders inputs for admin-defined attributes; the server performs authoritative validation. */
export function AttributeFields({ defs, value, onChange }: { defs: AttributeDefinition[]; value: Attributes; onChange: (v: Attributes) => void }) {
  const set = (name: string, v: string | number | boolean | undefined) => {
    const next = { ...value };
    if (v === undefined || v === "") delete next[name];
    else next[name] = v;
    onChange(next);
  };
  return (
    <>
      {defs.map((d) => {
        const label = `${d.label}${d.unit ? ` (${d.unit})` : ""}${d.required ? " *" : ""}`;
        const current = value[d.name];
        if (d.dataType === "boolean")
          return (
            <label key={d.name} className="checkbox">
              <input type="checkbox" checked={current === true} onChange={(e) => set(d.name, e.target.checked ? true : undefined)} />
              {label}
            </label>
          );
        if (d.dataType === "enum")
          return (
            <label key={d.name}>
              {label}
              <select value={typeof current === "string" ? current : ""} onChange={(e) => set(d.name, e.target.value)}>
                <option value="">Not set</option>
                {d.enumValues!.map((v) => (
                  <option key={v} value={v}>{v}</option>
                ))}
              </select>
            </label>
          );
        return (
          <label key={d.name}>
            {label}
            <input
              inputMode={d.dataType === "number" ? "decimal" : undefined}
              maxLength={d.maxLength ?? undefined}
              value={current === undefined ? "" : String(current)}
              onChange={(e) => set(d.name, e.target.value)}
            />
          </label>
        );
      })}
    </>
  );
}

export function formatAttribute(defs: AttributeDefinition[], name: string, value: unknown): [string, string] {
  const d = defs.find((x) => x.name === name);
  const text = typeof value === "boolean" ? (value ? "Yes" : "No") : String(value);
  return [d?.label ?? name, d?.unit ? `${text} ${d.unit}` : text];
}
