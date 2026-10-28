import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { AttributeFields, formatAttribute } from "./AttributeFields";
import { defaultItarMarkings, fromMarkings, toMarkingsDto } from "./MarkingsFields";
import type { AttributeDefinition, Attributes } from "../lib/api";

const defs: AttributeDefinition[] = [
  { name: "material", label: "Material", dataType: "string", unit: null, enumValues: null, required: false, maxLength: 120 },
  { name: "mass", label: "Mass", dataType: "number", unit: "kg", enumValues: null, required: false, maxLength: null },
  { name: "critical_item", label: "Critical item", dataType: "boolean", unit: null, enumValues: null, required: false, maxLength: null },
];

function Harness({ onValue }: { onValue: (v: Attributes) => void }) {
  const [v, setV] = useState<Attributes>({ material: "4340" });
  return <AttributeFields defs={defs} value={v} onChange={(n) => { setV(n); onValue(n); }} />;
}

describe("AttributeFields", () => {
  it("edits values and drops cleared ones", () => {
    let last: Attributes = {};
    render(<Harness onValue={(v) => (last = v)} />);
    fireEvent.change(screen.getByLabelText("Mass (kg)"), { target: { value: "1.5" } });
    fireEvent.click(screen.getByLabelText("Critical item"));
    fireEvent.change(screen.getByLabelText("Material"), { target: { value: "" } });
    expect(last).toEqual({ mass: "1.5", critical_item: true });
  });

  it("formats values with labels and units", () => {
    expect(formatAttribute(defs, "mass", 1.5)).toEqual(["Mass", "1.5 kg"]);
    expect(formatAttribute(defs, "critical_item", false)).toEqual(["Critical item", "No"]);
  });
});

describe("markings form mapping", () => {
  it("splits lists and nulls blanks, and round-trips", () => {
    const dto = toMarkingsDto({ ...defaultItarMarkings, cuiCategories: "EXPT, CTI ,", program: " ", eccn: "" });
    expect(dto.cuiCategories).toEqual(["EXPT", "CTI"]);
    expect(dto.program).toBeNull();
    expect(dto.eccn).toBeNull();
    const back = fromMarkings({ ...dto, exportJurisdiction: "Itar", distributionStatement: "D" });
    expect(back.cuiCategories).toBe("EXPT, CTI");
  });
});
