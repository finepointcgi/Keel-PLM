using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;

namespace Keel.Seed;

/// <summary>What happens to a part once its structure and documents are in place.</summary>
public enum Fate
{
    /// <summary>Released through the real review workflow (reviewer ada, approver bob, both signing with a fresh login).</summary>
    Released,
    /// <summary>Left checked in, In Work.</summary>
    InWork,
    /// <summary>Submitted for review; the reviewer's task is still open.</summary>
    UnderReview,
}

public sealed record PartDef(string Key, string Name, string Type, string Source, Markings Markings, string Description, JsonObject? Attributes,
    string Author, Fate Fate, string Unit = UnitsOfMeasure.Each);

public sealed record DocDef(string Key, string Name, string Type, Markings Markings, string Description, string FileName, string MediaType, string Author,
    Func<byte[]> Content);

public sealed record Line(string Child, decimal Qty, string? Unit = null, string[]? Refs = null);

/// <summary>
/// The fictional ORCA program (synthetic names only): an unmanned sensor pod, its sub-assemblies, machined parts, a purchased
/// electronics module, a standard-parts library, and the documents that describe them. Pure data: <see cref="DemoSeeder"/> runs it
/// through the services.
/// </summary>
public static class DemoCatalog
{
    private const string Office = "Keel Demo Engineering";

    // ---------------------------------------------------------------- markings
    public static Markings Itar(string program = "ORCA", params string[] authorizations) => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG",
        usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: Office, distributionReason: "Critical technology", program: program,
        exportAuthorizations: authorizations).Value;

    public static Markings Ear(string eccn, string? program = "ORCA") => Markings.Create(ExportJurisdiction.Ear, DistributionStatement.D, "ENG", eccn: eccn,
        cuiCategories: ["EXPT"], controllingOffice: Office, distributionReason: "Export controlled", program: program).Value;

    /// <summary>Open commercial item: EAR99, distribution A, no CUI.</summary>
    public static Markings Ear99 { get; } = Markings.Create(ExportJurisdiction.Ear, DistributionStatement.A, "ENG", eccn: Markings.Ear99).Value;

    /// <summary>No export jurisdiction at all (a public standard).</summary>
    public static Markings Open { get; } = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private static JsonObject Attr(string material, double mass, string? criticality = null, bool critical = false)
    {
        var o = new JsonObject { ["material"] = material, ["mass"] = mass };
        if (criticality is not null) o["criticality"] = criticality;
        if (critical) o["critical_item"] = true;
        return o;
    }

    // ---------------------------------------------------------------- parts (creation order = part number order)
    public static IReadOnlyList<PartDef> Parts { get; } = BuildParts();

    private static List<PartDef> BuildParts()
    {
        var list = new List<PartDef>();
        void A(string key, string name, Markings m, string desc, Fate fate = Fate.Released, string author = "alice", JsonObject? attrs = null) =>
            list.Add(new PartDef(key, name, "Assembly", "Make", m, desc, attrs, author, fate));
        void M(string key, string name, Markings m, string desc, string material, double mass, Fate fate = Fate.Released, string? crit = null, bool critical = false) =>
            list.Add(new PartDef(key, name, "Component", "Make", m, desc, Attr(material, mass, crit, critical), "alice", fate));
        void B(string key, string name, Markings m, string desc, double mass) =>
            list.Add(new PartDef(key, name, "Component", "Buy", m, desc, Attr("Purchased item", mass), "alice", Fate.Released));
        void S(string key, string name, string desc, string material, double mass) =>
            list.Add(new PartDef(key, name, "Standard", "Buy", Ear99, desc, Attr(material, mass), "alice", Fate.Released));

        // Assemblies, top down.
        A("top", "Unmanned sensor pod, ORCA", Itar(), "Top assembly of the synthetic ORCA program sensor pod.", Fate.InWork, attrs: Attr("Assembly", 11.8, "Major"));
        A("housing", "Pressure housing assembly", Itar(), "Sealed housing that carries the gimbal and protects the payload.");
        A("payload", "Sensor payload assembly", Itar(), "Electro-optical sensor payload and its optical mounting.", Fate.InWork);
        A("avionics", "Avionics bay assembly", Ear("3A611.x"), "Flight controller, power distribution and battery.", Fate.UnderReview);
        A("partner", "Partner mounting interface assembly", Itar("ORCA", "TAA-0042"), "Mounting interface shared with the licensed partner (TAA-0042).", Fate.InWork);
        A("gimbal", "Gimbal assembly", Itar(), "Two-axis gimbal that points the sensor.");
        A("drive", "Gimbal drive module", Itar(), "Motor, gear train and encoder of one gimbal axis.");
        A("kit-ext", "Fastener kit, pod exterior", Ear99, "Standard fasteners for the pod exterior.");
        A("kit-hsg", "Fastener kit, housing", Ear99, "Standard fasteners for the pressure housing.");
        A("spares", "Field spares kit, ORCA pod", Ear99, "Open spares list: commercial items and a few restricted spares (restricted lines are hidden from people who may not see them).");

        // Machined parts (ITAR, program ORCA).
        M("shell-fwd", "Housing shell, forward", Itar(), "Forward half of the pressure housing, machined from bar.", "6061-T6", 1.42);
        M("shell-aft", "Housing shell, aft", Itar(), "Aft half of the pressure housing.", "6061-T6", 1.65);
        M("endcap", "End cap, sensor window", Itar(), "Holds the sapphire window in the forward shell.", "7075-T6", 0.38, crit: "Major");
        M("retainer", "Retaining ring, housing", Itar(), "Locks the end cap.", "17-4PH", 0.07);
        M("bracket", "Gimbal bracket", Itar(), "Holds the sensor gimbal. Wall thickness is under review in ECN-000001.", "7075-T6", 0.31, crit: "Critical safety", critical: true);
        M("yoke", "Gimbal yoke", Itar(), "Carries the inner gimbal axis.", "7075-T6", 0.27);
        M("shaft", "Shaft pin, 6 mm", Itar(), "Gimbal axis shaft.", "17-4PH", 0.02);
        M("gear", "Gear, spur 48T", Itar(), "Output gear of the drive module.", "17-4PH", 0.06);
        M("pinion", "Pinion, 16T", Itar(), "Motor pinion.", "17-4PH", 0.02);
        M("motor-plate", "Motor mount plate", Itar(), "Mounts the motor to the yoke.", "6061-T6", 0.05);
        M("lens-barrel", "Lens barrel", Itar(), "Optical barrel of the sensor.", "Ti-6Al-4V", 0.22, crit: "Major");
        M("lens-ring", "Lens retaining ring", Itar(), "Retains the front lens element.", "Ti-6Al-4V", 0.02);
        M("mount-plate", "Sensor mounting plate", Itar(), "Mounts the sensor module on the gimbal.", "7075-T6", 0.19, crit: "Major");
        M("shim-set", "Optical alignment shim set", Itar(), "Shims that align the optical axis.", "304 stainless", 0.01);
        M("harness", "Wiring harness, main", Itar(), "Power and signal harness between bays.", "Copper / PTFE", 0.44);
        M("chassis", "Chassis, avionics", Ear("9A610.x"), "Machined chassis of the avionics bay.", "6061-T6", 0.52);
        M("heatsink", "Heat sink, flight controller", Ear("9A610.x"), "Conducts controller heat into the chassis.", "6063-T5", 0.09);
        M("cal-holder", "Calibration target holder", Ear("9A610.x"), "Bench holder for payload calibration.", "6061-T6", 0.12);
        M("plate-tx", "Mounting interface plate", Itar("ORCA", "TAA-0042"), "Interface plate shared with the licensed partner.", "7075-T6", 0.33);
        M("ring-tx", "Interface adapter ring", Itar("ORCA", "TAA-0042"), "Adapter between the pod and the partner structure.", "7075-T6", 0.14);

        // Purchased modules (EAR with classifications; the electronics module is the interesting one).
        B("sensor", "Optical sensor module, EO/IR", Ear("6A003.b"), "Purchased electro-optical and infrared sensor module.", 0.85);
        B("window", "Window, sapphire", Ear("6A004.a"), "Purchased sapphire window, 80 mm.", 0.06);
        B("fc", "Flight controller module", Ear("3A611.a"), "Purchased flight controller electronics module.", 0.11);
        B("pdb", "Power distribution board", Ear("3A611.x"), "Purchased power distribution board.", 0.08);
        B("battery", "Battery pack, 14.8 V", Ear("3A611.x"), "Purchased lithium battery pack.", 0.62);
        B("motor", "Brushless motor, frameless", Ear("9A610.x"), "Purchased frameless gimbal motor.", 0.09);
        B("encoder", "Encoder, magnetic", Ear("3A611.x"), "Purchased magnetic absolute encoder.", 0.01);

        // Raw material (consumed in a purchased quantity).
        list.Add(new PartDef("coating", "Conformal coating", "RawMaterial", "Buy", Ear99, "Protective coating for the avionics boards.", Attr("Acrylic", 0.0), "alice", Fate.Released, "mL"));

        // The KRAKEN part: only people on program KRAKEN can see it.
        list.Add(new PartDef("kraken", "Cooler interface block (KRAKEN)", "Component", "Make", Itar("KRAKEN"), "Interface block of another program's cooler; invisible to ORCA-only users.",
            Attr("6061-T6", 0.08), "kraken-eng", Fate.InWork));

        // Standard-parts library (open commercial items, EAR99).
        S("scr-m3x6", "Screw, socket head, M3x6", "Socket head cap screw M3x6, A2 stainless.", "A2 stainless", 0.001);
        S("scr-m3x8", "Screw, socket head, M3x8", "Socket head cap screw M3x8, A2 stainless.", "A2 stainless", 0.001);
        S("scr-m3x10", "Screw, socket head, M3x10", "Socket head cap screw M3x10, A2 stainless.", "A2 stainless", 0.001);
        S("scr-m4x12", "Screw, socket head, M4x12", "Socket head cap screw M4x12, A2 stainless.", "A2 stainless", 0.003);
        S("scr-m5x16", "Screw, socket head, M5x16", "Socket head cap screw M5x16, A2 stainless.", "A2 stainless", 0.006);
        S("wsh-m3", "Washer, flat, M3", "Flat washer M3.", "A2 stainless", 0.0003);
        S("wsh-m4", "Washer, flat, M4", "Flat washer M4.", "A2 stainless", 0.0005);
        S("wsh-m5", "Washer, flat, M5", "Flat washer M5.", "A2 stainless", 0.0008);
        S("nut-m4", "Locknut, nylon insert, M4", "Nylon insert locknut M4.", "A2 stainless", 0.002);
        S("nut-m5", "Locknut, nylon insert, M5", "Nylon insert locknut M5.", "A2 stainless", 0.003);
        S("dowel", "Dowel pin, 4 mm", "Hardened dowel pin 4 mm.", "Steel", 0.004);
        S("oring-120", "O-ring, 120 mm", "O-ring 120 mm, FKM.", "FKM", 0.012);
        S("oring-60", "O-ring, 60 mm", "O-ring 60 mm, FKM.", "FKM", 0.006);
        S("brg-6x12", "Bearing, flanged, 6x12", "Flanged ball bearing 6x12 mm.", "Steel", 0.004);
        S("brg-10x26", "Bearing, deep groove, 10x26", "Deep groove ball bearing 10x26 mm.", "Steel", 0.019);
        S("gland", "Cable gland, M12", "Cable gland M12, nickel plated brass.", "Brass", 0.015);
        S("vent", "Breather vent, M12", "Waterproof breather vent.", "PTFE / nylon", 0.004);
        S("anode", "Zinc anode, 25 mm", "Sacrificial zinc anode.", "Zinc", 0.03);
        S("desiccant", "Desiccant pack", "Silica gel desiccant pack, 5 g.", "Silica gel", 0.005);
        S("pad", "Thermal isolator pad", "Glass epoxy thermal isolator pad.", "G10", 0.002);
        S("standoff", "Standoff, M3x10", "Hex standoff M3x10.", "Aluminum", 0.002);
        S("conn", "Connector, 12 pin", "Circular 12 pin connector.", "Aluminum / gold", 0.021);
        S("tie", "Cable tie, nylon", "Nylon cable tie 100 mm.", "Nylon 66", 0.0005);
        S("nameplate", "Nameplate, data", "Anodized data nameplate.", "Aluminum", 0.003);
        return list;
    }

    // ---------------------------------------------------------------- structure (parent -> lines)
    public static IReadOnlyDictionary<string, Line[]> Structure { get; } = new Dictionary<string, Line[]>(StringComparer.Ordinal)
    {
        ["top"] = [new("housing", 1), new("payload", 1), new("avionics", 1), new("partner", 1), new("harness", 1), new("nameplate", 1), new("kit-ext", 1), new("desiccant", 2)],
        ["housing"] =
        [
            new("shell-fwd", 1), new("shell-aft", 1), new("endcap", 1), new("window", 1), new("retainer", 1), new("oring-120", 2), new("oring-60", 2),
            new("gimbal", 1), new("gland", 2), new("vent", 1), new("anode", 1), new("kit-hsg", 1),
        ],
        ["gimbal"] =
        [
            new("bracket", 1), new("yoke", 1), new("drive", 1), new("brg-10x26", 2), new("shaft", 2), new("scr-m4x12", 8), new("wsh-m4", 8), new("nut-m4", 4),
        ],
        ["drive"] =
        [
            new("motor", 1), new("gear", 1), new("pinion", 1), new("encoder", 1), new("brg-6x12", 2), new("motor-plate", 1), new("scr-m3x8", 8), new("wsh-m3", 8),
        ],
        ["payload"] =
        [
            new("sensor", 1), new("lens-barrel", 1), new("lens-ring", 1), new("mount-plate", 1), new("pad", 4), new("cal-holder", 1), new("shim-set", 1),
            new("scr-m3x10", 12), new("wsh-m3", 12),
        ],
        ["avionics"] =
        [
            new("fc", 1), new("pdb", 1), new("battery", 1), new("chassis", 1), new("heatsink", 1), new("standoff", 8), new("conn", 4, null, ["J1", "J2", "J3", "J4"]),
            new("tie", 10), new("scr-m3x6", 16), new("wsh-m3", 16), new("coating", 25, "mL"),
        ],
        ["partner"] = [new("plate-tx", 1), new("ring-tx", 1), new("scr-m5x16", 6), new("wsh-m5", 6), new("dowel", 2)],
        ["kit-ext"] = [new("scr-m5x16", 12), new("wsh-m5", 12), new("nut-m5", 12)],
        ["kit-hsg"] = [new("scr-m4x12", 24), new("wsh-m4", 24), new("nut-m4", 24)],
        // Open assembly whose restricted lines are redacted for lena, carmen and (partly) farid: the redaction demonstration.
        ["spares"] = [new("scr-m5x16", 20), new("desiccant", 10), new("oring-120", 4), new("bracket", 2), new("mount-plate", 1), new("plate-tx", 1)],
    };

    /// <summary>The KRAKEN engineer adds this line in a final session, because only someone on KRAKEN may read the child.</summary>
    public static (string Parent, Line Line) KrakenLine { get; } = ("payload", new Line("kraken", 1));

    // ---------------------------------------------------------------- documents
    public static IReadOnlyList<DocDef> Documents { get; } =
    [
        Doc("spec-system", "ORCA sensor pod system specification", DocumentTypes.All[0], Itar(), "System-level requirements of the synthetic sensor pod.",
            ["1. Scope: unmanned sensor pod for the ORCA program (synthetic).", "2. Pointing accuracy: 0.5 mrad (synthetic value).", "3. Operating depth: 200 m (synthetic value).", "4. Mass: below 12 kg."]),
        Doc("dwg-bracket", "Gimbal bracket drawing", DocumentTypes.All[1], Itar(), "Dimensioned drawing of the gimbal bracket.",
            ["Part: gimbal bracket. Material 7075-T6.", "Wall thickness 2.0 mm (revision A).", "Tolerance +/-0.05 mm unless noted.", "Finish: anodize, MIL-A-8625 Type II."]),
        Doc("dwg-gimbal", "Gimbal assembly drawing", DocumentTypes.All[1], Itar(), "Assembly drawing of the two-axis gimbal.",
            ["Assembly: gimbal. See bill of materials.", "Torque M4 fasteners to 2.5 Nm.", "Bearing preload: 5 N."]),
        Doc("dwg-housing", "Pressure housing assembly drawing", DocumentTypes.All[1], Itar(), "Assembly drawing of the pressure housing.",
            ["Assembly: pressure housing.", "O-ring groove finish 0.8 um Ra.", "Leak test at 25 bar for 10 minutes."]),
        Doc("dwg-mount", "Sensor mounting plate drawing", DocumentTypes.All[1], Itar(), "Drawing of the sensor mounting plate.",
            ["Part: sensor mounting plate. Material 7075-T6.", "Flatness 0.02 mm.", "Hole pattern per ICD-ORCA-02."]),
        Doc("icd-payload", "Sensor payload interface control document", DocumentTypes.All[0], Itar(), "Mechanical and electrical interface of the sensor payload.",
            ["Mechanical: 4 x M3 on a 40 mm square.", "Electrical: 12 pin connector, 14.8 V.", "Thermal: conduction to the chassis."]),
        Doc("cad-bracket", "Gimbal bracket CAD model (placeholder)", DocumentTypes.All[2], Itar(), "Placeholder for the native CAD model. CAD integration is Phase 4.",
            null, "gimbal-bracket.step", "application/step"),
        Doc("proc-assembly", "Pod final assembly procedure", DocumentTypes.All[4], Itar(), "Step-by-step final assembly of the pod.",
            ["Step 1: install O-rings dry-fit, then greased.", "Step 2: seat the gimbal in the forward shell.", "Step 3: torque fasteners per drawing.", "Step 4: pressure test."]),
        Doc("dwg-harness", "Wiring harness drawing", DocumentTypes.All[1], Ear("3A611.x"), "Harness routing and connector table.",
            ["Harness: main. Connectors J1 to J4.", "Wire gauge 22 AWG PTFE.", "Continuity test per procedure."]),
        Doc("std-torque", "Fastener installation torque standard", DocumentTypes.All[0], Open, "Open shop standard for fastener torque.",
            ["M3: 1.0 Nm.", "M4: 2.5 Nm.", "M5: 5.0 Nm.", "All values synthetic."]),
        Doc("dwg-partner", "Partner interface drawing", DocumentTypes.All[1], Itar("ORCA", "TAA-0042"), "Interface drawing shared with the licensed partner (TAA-0042).",
            ["Part: mounting interface plate.", "Bolt circle 120 mm.", "Shared under TAA-0042 (synthetic)."]),
        Doc("rpt-vibe", "Vibration test report, gimbal bracket", DocumentTypes.All[3], Itar(), "Test report that motivated the bracket change.",
            ["Test: random vibration, 3.1 g RMS (synthetic).", "Result: micro-cracks at the bracket radius after 30 minutes.", "Recommendation: increase wall thickness."]),
    ];

    private static DocDef Doc(string key, string name, string type, Markings markings, string description, string[]? lines, string? fileName = null, string? mediaType = null) =>
        new(key, name, type, markings, description, fileName ?? key + ".pdf", mediaType ?? "application/pdf", "alice",
            lines is null
                ? () => System.Text.Encoding.UTF8.GetBytes("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('Synthetic placeholder, not a real CAD model'),'2;1');\nENDSEC;\nEND-ISO-10303-21;\n")
                : () => SyntheticPdf.Create(name, lines));

    /// <summary>Links added to a part's working copy: part key -> (document key, link type).</summary>
    public static IReadOnlyDictionary<string, (string Doc, string Type)[]> Links { get; } = new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
    {
        ["top"] = [("spec-system", LinkTypes.References), ("proc-assembly", LinkTypes.References)],
        ["housing"] = [("dwg-housing", LinkTypes.Describes)],
        ["gimbal"] = [("dwg-gimbal", LinkTypes.Describes)],
        ["bracket"] = [("dwg-bracket", LinkTypes.Describes), ("cad-bracket", LinkTypes.Describes), ("rpt-vibe", LinkTypes.References)],
        ["mount-plate"] = [("dwg-mount", LinkTypes.Describes)],
        ["payload"] = [("icd-payload", LinkTypes.References)],
        ["harness"] = [("dwg-harness", LinkTypes.Describes)],
        ["kit-ext"] = [("std-torque", LinkTypes.References)],
        ["kit-hsg"] = [("std-torque", LinkTypes.References)],
        ["plate-tx"] = [("dwg-partner", LinkTypes.Describes)],
    };
}
