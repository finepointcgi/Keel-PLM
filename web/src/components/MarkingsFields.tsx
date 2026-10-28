import type { Markings } from "../lib/api";

/** Form state for markings; list fields are edited as comma-separated text. */
export interface MarkingsInput {
  exportJurisdiction: string;
  eccn: string;
  usmlCategory: string;
  cuiCategories: string;
  distributionStatement: string;
  controllingOffice: string;
  distributionReason: string;
  program: string;
  owningOrg: string;
  exportAuthorizations: string;
}

const split = (s: string) => s.split(",").map((x) => x.trim()).filter(Boolean);
const orNull = (s: string) => (s.trim() ? s.trim() : null);

export function toMarkingsDto(m: MarkingsInput) {
  return {
    exportJurisdiction: m.exportJurisdiction,
    distributionStatement: m.distributionStatement,
    owningOrg: m.owningOrg,
    eccn: orNull(m.eccn),
    usmlCategory: orNull(m.usmlCategory),
    cuiCategories: split(m.cuiCategories),
    controllingOffice: orNull(m.controllingOffice),
    distributionReason: orNull(m.distributionReason),
    program: orNull(m.program),
    exportAuthorizations: split(m.exportAuthorizations),
  };
}

export function fromMarkings(m: Markings): MarkingsInput {
  return {
    exportJurisdiction: m.exportJurisdiction,
    eccn: m.eccn ?? "",
    usmlCategory: m.usmlCategory ?? "",
    cuiCategories: m.cuiCategories.join(", "),
    distributionStatement: m.distributionStatement,
    controllingOffice: m.controllingOffice ?? "",
    distributionReason: m.distributionReason ?? "",
    program: m.program ?? "",
    owningOrg: m.owningOrg,
    exportAuthorizations: m.exportAuthorizations.join(", "),
  };
}

export const defaultItarMarkings: MarkingsInput = {
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
};

export function MarkingsFields({ value, onChange }: { value: MarkingsInput; onChange: (v: MarkingsInput) => void }) {
  const set = (k: keyof MarkingsInput) => (e: { target: { value: string } }) => onChange({ ...value, [k]: e.target.value });
  return (
    <>
      <label>
        Export jurisdiction
        <select value={value.exportJurisdiction} onChange={set("exportJurisdiction")}>
          <option value="None">None</option>
          <option value="Ear">EAR</option>
          <option value="Itar">ITAR</option>
        </select>
      </label>
      <label>
        ECCN (EAR)
        <input value={value.eccn} onChange={set("eccn")} placeholder="EAR99 or e.g. 9E610.a" disabled={value.exportJurisdiction !== "Ear"} />
      </label>
      <label>
        USML category (ITAR)
        <input value={value.usmlCategory} onChange={set("usmlCategory")} disabled={value.exportJurisdiction !== "Itar"} />
      </label>
      <label>
        CUI categories (comma-separated)
        <input value={value.cuiCategories} onChange={set("cuiCategories")} />
      </label>
      <label>
        Distribution statement
        <select value={value.distributionStatement} onChange={set("distributionStatement")}>
          {["A", "B", "C", "D", "E", "F"].map((s) => (
            <option key={s} value={s}>{s}</option>
          ))}
        </select>
      </label>
      <label>
        Controlling office
        <input value={value.controllingOffice} onChange={set("controllingOffice")} />
      </label>
      <label>
        Distribution reason
        <input value={value.distributionReason} onChange={set("distributionReason")} />
      </label>
      <label>
        Program
        <input value={value.program} onChange={set("program")} />
      </label>
      <label>
        Owning organization
        <input value={value.owningOrg} onChange={set("owningOrg")} />
      </label>
      <label>
        Export authorizations (comma-separated)
        <input value={value.exportAuthorizations} onChange={set("exportAuthorizations")} placeholder="e.g. TAA-0042" />
      </label>
    </>
  );
}

export function MarkingChip({ text }: { text: string }) {
  return <span className={`chip ${text.startsWith("CUI") ? "tone-cui" : "tone-unclassified"}`}>{text}</span>;
}
