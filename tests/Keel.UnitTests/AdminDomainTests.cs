using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Core;
using Keel.Web.Services;

namespace Keel.UnitTests;

/// <summary>Pure administration rules (ADR-0022): lifecycle validation, attribute definitions, the class tree, import formats and drafts.</summary>
public sealed class AdminDomainTests
{
    // ---------------------------------------------------------------- lifecycle rules

    private static LifecycleDefinition PartLifecycle() => new("part-default", "Standard part lifecycle", "part", 1, "published",
    [
        new("InWork", "In Work", 10, true, false, false), new("UnderReview", "Under Review", 20, false, true, false),
        new("Released", "Released", 30, false, true, false), new("Obsolete", "Obsolete", 40, false, true, true),
    ],
    [
        new("InWork", "UnderReview", "workflow"), new("UnderReview", "InWork", "workflow"), new("UnderReview", "Released", "workflow"),
        new("Released", "Obsolete", "change"), new("InWork", "Obsolete", "change"),
    ]);

    [Fact]
    public void The_seeded_part_lifecycle_is_valid()
    {
        var v = LifecycleRules.Validate(PartLifecycle(), workflowBound: true);
        Assert.Empty(v.Errors);
    }

    [Fact]
    public void Lifecycle_rules_report_each_structural_problem()
    {
        var d = PartLifecycle();
        string[] Errors(LifecycleDefinition x, bool bound = true) => [.. LifecycleRules.Validate(x, bound, d).Errors];

        Assert.Contains(Errors(d with { States = [.. d.States.Select(s => s with { IsInitial = false })] }), e => e.Contains("Exactly one initial state is required (found 0)", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { States = [.. d.States.Select(s => s.State == "InWork" ? s with { IsLocked = true } : s)] }), e => e.Contains("cannot be locked", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { States = [.. d.States, new LifecycleStateDef("Lost", "Lost", 90, false, false, false)] }), e => e.Contains("not reachable from the initial state: Lost", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { Transitions = [.. d.Transitions, new LifecycleTransitionDef("InWork", "Gone", "manual")] }), e => e.Contains("ends at a state that does not exist", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { Transitions = [.. d.Transitions, new LifecycleTransitionDef("InWork", "InWork", "manual")] }), e => e.Contains("loops onto itself", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { Transitions = [.. d.Transitions, new LifecycleTransitionDef("InWork", "UnderReview", "manual")] }), e => e.Contains("defined more than once", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { Transitions = [.. d.Transitions, new LifecycleTransitionDef("InWork", "Released", "sometimes")] }), e => e.Contains("unknown tag", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { States = [.. d.States.Select(s => s with { IsLocked = false })] }), e => e.Contains("needs at least one locked state", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { States = [.. d.States, new LifecycleStateDef("InWork", "Again", 50, false, false, false)] }), e => e.Contains("used more than once", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { States = [.. d.States, new LifecycleStateDef("Bad Key", "Bad", 50, false, false, false)], Name = "" }), e => e.Contains("must start with a letter", StringComparison.Ordinal));
        Assert.Contains(Errors(d with { Name = "" }), e => e.Contains("name is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Workflow_tags_need_a_bound_template_unless_the_version_already_had_them_and_protected_parts_stay()
    {
        var d = PartLifecycle();
        var added = d with { States = [.. d.States, new LifecycleStateDef("Qualified", "Qualified", 25, false, false, false)], Transitions = [.. d.Transitions, new LifecycleTransitionDef("InWork", "Qualified", "workflow"), new LifecycleTransitionDef("Qualified", "InWork", "manual")] };
        Assert.Contains(LifecycleRules.Validate(added, workflowBound: false, d).Errors, e => e.Contains("no workflow template is active for part", StringComparison.Ordinal));
        Assert.Empty(LifecycleRules.Validate(added, workflowBound: true, d).Errors);
        Assert.Empty(LifecycleRules.Validate(d, workflowBound: false, d).Errors); // inherited workflow transitions are not re-checked

        var noRelease = d with { States = [.. d.States.Where(s => s.State != "Released")], Transitions = [.. d.Transitions.Where(t => t.From != "Released" && t.To != "Released")] };
        Assert.Contains(LifecycleRules.Validate(noRelease, true, d).Errors, e => e.Contains("State 'Released' cannot be removed", StringComparison.Ordinal));
        var unlockedRelease = d with { States = [.. d.States.Select(s => s.State == "Released" ? s with { IsLocked = false } : s)] };
        Assert.Contains(LifecycleRules.Validate(unlockedRelease, true, d).Errors, e => e.Contains("'Released' must stay locked", StringComparison.Ordinal));
        var manualRelease = d with { Transitions = [.. d.Transitions.Select(t => t is { From: "UnderReview", To: "Released" } ? t with { Via = "manual" } : t)] };
        Assert.Contains(LifecycleRules.Validate(manualRelease, true, d).Errors, e => e.Contains("workflow transition UnderReview → Released is required", StringComparison.Ordinal));
        // Other object types have no protected states.
        Assert.Empty(LifecycleRules.ProtectedStates("widget"));
    }

    [Fact]
    public void The_diff_names_what_publishing_changes()
    {
        var d = PartLifecycle();
        var next = d with
        {
            States = [.. d.States.Select(s => s.State == "Obsolete" ? s with { Label = "Retired", IsFinal = false } : s), new LifecycleStateDef("Qualified", "Qualified", 25, false, false, false)],
            Transitions = [.. d.Transitions.Where(t => t.From != "InWork" || t.To != "Obsolete"), new LifecycleTransitionDef("InWork", "Qualified", "manual")],
        };
        var effects = LifecycleRules.Diff(d, next);
        Assert.Contains("Added state Qualified (Qualified).", effects);
        Assert.Contains("Renamed Obsolete to Retired.", effects);
        Assert.Contains("Retired is now not final.", effects);
        Assert.Contains("Added transition InWork → Qualified (manual).", effects);
        Assert.Contains("Removed transition InWork → Obsolete.", effects);
        Assert.Empty(LifecycleRules.Diff(d, d));
    }

    [Fact]
    public void A_state_mapping_must_cover_every_state_in_use_and_never_unlock()
    {
        var older = PartLifecycle();
        var newer = older with { Version = 2, States = [.. older.States, new LifecycleStateDef("Qualified", "Qualified", 25, false, false, false)] };
        StateUsage[] usage = [new("InWork", 3, 1), new("Released", 2, 0), new("Obsolete", 0, 0)];
        Assert.Empty(LifecycleRules.ValidateMapping(older, newer, new Dictionary<string, string> { ["InWork"] = "Qualified", ["Released"] = "Released" }, usage));
        Assert.Contains("needs a target state", LifecycleRules.ValidateMapping(older, newer, new Dictionary<string, string>(), usage)[0], StringComparison.Ordinal);
        Assert.Contains(LifecycleRules.ValidateMapping(older, newer, new Dictionary<string, string> { ["InWork"] = "InWork", ["Released"] = "Qualified" }, usage), e => e.Contains("would unlock released data", StringComparison.Ordinal));
        Assert.Contains(LifecycleRules.ValidateMapping(older, newer, new Dictionary<string, string> { ["InWork"] = "UnderReview", ["Released"] = "Released" }, usage), e => e.Contains("1 revision(s) in InWork are checked out", StringComparison.Ordinal));
        Assert.Contains(LifecycleRules.ValidateMapping(older, newer, new Dictionary<string, string> { ["InWork"] = "Nowhere", ["Released"] = "Released" }, usage), e => e.Contains("does not exist in version 2", StringComparison.Ordinal));
        Assert.Contains(LifecycleRules.ValidateMapping(older, newer, new Dictionary<string, string> { ["InWork"] = "InWork", ["Released"] = "Released", ["Ghost"] = "Released" }, usage), e => e.Contains("Mapped state 'Ghost'", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- attribute definitions

    private static AttributeDefinitionDto Existing(string type = "number", long inUse = 0, string[]? enums = null) =>
        new("part", "gauge", "Gauge", type, null, enums, false, null, 0, null, null, null, null, null, false, 1, inUse);

    [Fact]
    public void Attribute_input_rules()
    {
        Assert.Empty(AttributeRules.Validate(new AttributeDefinitionInput("diameter", "Diameter", "number", "mm", Min: 1, Max: 100), null));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("Diameter", "Diameter", "number"), null), e => e.Contains("name must start", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "colour"), null), e => e.Contains("data type must be one of", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "enum"), null), e => e.Contains("at least one value", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "enum", EnumValues: ["x", "x"]), null), e => e.Contains("must be unique", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "string", EnumValues: ["x"]), null), e => e.Contains("Only enum attributes have values", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "number", MaxLength: 5), null), e => e.Contains("maximum length applies to text", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "string", Pattern: "(unclosed"), null), e => e.Contains("not a valid regular expression", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "number", Pattern: "x"), null), e => e.Contains("applies to text attributes only", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "string", Min: 1), null), e => e.Contains("range applies to number", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "number", Min: 5, Max: 1), null), e => e.Contains("minimum must not exceed", StringComparison.Ordinal));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("a", "A", "string", Unit: "kg"), null), e => e.Contains("Only number attributes have a unit", StringComparison.Ordinal));
    }

    [Fact]
    public void The_data_type_of_an_attribute_with_values_is_fixed_and_enum_values_in_use_stay()
    {
        var number = new AttributeDefinitionInput("gauge", "Gauge", "number");
        Assert.Empty(AttributeRules.Validate(number with { Label = "Wire gauge" }, Existing(inUse: 4)));
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("gauge", "Gauge", "string"), Existing(inUse: 4)), e => e.Contains("4 stored value(s)", StringComparison.Ordinal));
        Assert.Empty(AttributeRules.Validate(new AttributeDefinitionInput("gauge", "Gauge", "string"), Existing(inUse: 0))); // free to change while nothing is stored
        var enumDef = Existing("enum", 2, ["A", "B"]);
        var inUse = new HashSet<string> { "B" };
        Assert.Contains(AttributeRules.Validate(new AttributeDefinitionInput("gauge", "Gauge", "enum", EnumValues: ["A"]), enumDef, inUse), e => e.Contains('B'));
        Assert.Empty(AttributeRules.Validate(new AttributeDefinitionInput("gauge", "Gauge", "enum", EnumValues: ["B", "C"]), enumDef, inUse));
    }

    [Fact]
    public void Shared_attribute_validation_applies_format_range_retirement_and_required()
    {
        var specs = new[]
        {
            new AttributeSpec("ref", "Reference", "string", null, null, false, 20, 1, HelpText: "DRW-nnnn", Pattern: "DRW-[0-9]{4}"),
            new AttributeSpec("dia", "Diameter", "number", "mm", null, true, null, 2, Min: 1, Max: 100),
            new AttributeSpec("old", "Old", "enum", null, ["x"], true, null, 3, Retired: true),
        };
        var (ok, errors) = AttributeSpecs.Validate(specs, new JsonObject { ["ref"] = "DRW-0042", ["dia"] = 12.5, ["old"] = "anything" });
        Assert.Empty(errors);
        Assert.Equal("""{"dia":12.5,"old":"anything","ref":"DRW-0042"}""", ok.ToJsonString()); // a retired value is carried unchanged, never required
        Assert.Contains("does not match the required format (DRW-nnnn)", AttributeSpecs.Validate(specs, new JsonObject { ["ref"] = "X", ["dia"] = 5 }).Errors[0], StringComparison.Ordinal);
        Assert.Contains("must be at least 1", AttributeSpecs.Validate(specs, new JsonObject { ["dia"] = 0 }).Errors[0], StringComparison.Ordinal);
        Assert.Contains("must be at most 100", AttributeSpecs.Validate(specs, new JsonObject { ["dia"] = "101" }).Errors[0], StringComparison.Ordinal);
        Assert.Equal(["Diameter is required."], AttributeSpecs.Validate(specs, new JsonObject()).Errors);
        Assert.Equal(["Unknown attribute 'nope'."], AttributeSpecs.Validate(specs, new JsonObject { ["dia"] = 5, ["nope"] = 1 }).Errors);
    }

    // ---------------------------------------------------------------- class tree

    private static ClassTree Tree()
    {
        ClassNodeDto N(string code, string? parent, int depth, string path, string status = "active") =>
            new(Guid.NewGuid(), code, code + " name", "", null, path, depth, 0, status);
        var mech = N("MECH", null, 1, "/MECH/");
        var fast = N("FAST", "MECH", 2, "/MECH/FAST/") with { ParentId = mech.Id };
        var bolt = N("BOLT", "FAST", 3, "/MECH/FAST/BOLT/") with { ParentId = fast.Id };
        var elec = N("ELEC", null, 1, "/ELEC/");
        AttributeDefinitionDto A(ClassNodeDto n, string name, int sort = 0, bool retired = false) =>
            new(n.Id.ToString(), name, name.ToUpperInvariant(), "number", null, null, false, null, sort, null, null, null, null, null, retired, 1, 0);
        return new ClassTree([mech, fast, bolt, elec], [A(mech, "grade"), A(fast, "diameter", 2), A(fast, "thread", 1), A(bolt, "length")]);
    }

    [Fact]
    public void The_effective_set_is_ancestors_first_and_ordered_within_a_class()
    {
        var tree = Tree();
        var bolt = tree.ByCode("bolt")!;
        Assert.Equal([("grade", "MECH"), ("thread", "FAST"), ("diameter", "FAST"), ("length", "BOLT")], tree.Effective(bolt.Id).Select(e => (e.Definition.Name, e.DefinedAt)));
        Assert.Equal(["MECH", "FAST", "BOLT"], tree.Chain(bolt.Id).Select(n => n.Code));
        Assert.Equal(["MECH", "FAST", "BOLT"], tree.Subtree(tree.ByCode("MECH")!.Id).Select(n => n.Code));
        Assert.Equal(2, tree.Height(tree.ByCode("MECH")!.Id));
        Assert.Equal(0, tree.Height(bolt.Id));
        Assert.Empty(tree.Effective(tree.ByCode("ELEC")!.Id));
        Assert.Equal("/MECH/FAST/", ClassTree.PathOf(tree.Chain(tree.ByCode("FAST")!.Id)));
        Assert.Equal(["ELEC", "MECH"], tree.Children(null).Select(n => n.Code).Order());
    }

    [Fact]
    public void Attribute_names_must_be_unique_along_a_path_and_a_move_checks_the_new_ancestors()
    {
        var tree = Tree();
        var fast = tree.ByCode("FAST")!;
        Assert.Contains("already defines", tree.NameClash(fast.Id, "diameter")!, StringComparison.Ordinal);
        Assert.Contains("ancestor MECH", tree.NameClash(fast.Id, "grade")!, StringComparison.Ordinal);
        Assert.Contains("subclass BOLT", tree.NameClash(fast.Id, "length")!, StringComparison.Ordinal);
        Assert.Null(tree.NameClash(tree.ByCode("ELEC")!.Id, "length")); // another branch may reuse a name
        // Moving FAST under ELEC is fine; ELEC gaining "length" would then clash with BOLT below FAST.
        Assert.Empty(tree.NameClashesWhenPlacedUnder(fast.Id, tree.ByCode("ELEC")!.Id));
        Assert.Empty(tree.NameClashesWhenPlacedUnder(fast.Id, null));
    }

    // ---------------------------------------------------------------- formats and drafts

    [Fact]
    public void Csv_fields_with_commas_quotes_and_line_breaks_round_trip()
    {
        var doc = new ClassHierarchyDoc(ClassHierarchyDoc.CurrentFormat,
        [
            new ClassNodeDoc("A", "Name, with comma", "Say \"hi\"\nsecond line", null, 5, "active", [new AttributeDoc("x", "X", "enum", EnumValues: ["a b", "c,d"], HelpText: "h")]),
        ]);
        var (back, error) = ClassificationIO.FromCsv(ClassificationIO.ToCsv(doc));
        Assert.Null(error);
        Assert.Equal("Name, with comma", back!.Nodes[0].Name);
        Assert.Equal("Say \"hi\"\nsecond line", back.Nodes[0].Description);
        Assert.Equal(["a b", "c,d"], back.Nodes[0].Attributes[0].EnumValues);
        Assert.Equal(5, back.Nodes[0].SortOrder);
    }

    [Fact]
    public void Lifecycle_drafts_convert_back_and_forth_and_removing_a_state_removes_its_transitions()
    {
        var d = LifecycleDraft.From(PartLifecycle());
        Assert.Equal("InWork", d.Initial);
        Assert.Equal(PartLifecycle().States.Select(s => s.State), d.ToDefinition().States.Select(s => s.State));
        d.AddState();
        d.States[^1].State = "Qualified";
        d.States[^1].Label = "Qualified";
        d.AddTransition();
        Assert.Equal(5, d.ToDefinition().States.Count);
        Assert.Equal(6, d.ToDefinition().Transitions.Count);
        d.RemoveState(d.States[^1]);
        Assert.Equal(4, d.States.Count);
        Assert.DoesNotContain(d.Transitions, t => t.From == "Qualified" || t.To == "Qualified");
        Assert.Equal(1, d.ToDefinition().States.Count(s => s.IsInitial));
    }

    [Fact]
    public void Facet_selection_becomes_search_filters()
    {
        var s = new FacetSelection();
        Assert.True(s.IsEmpty);
        s.Toggle("grade", "316", true);
        s.Toggle("grade", "304", true);
        s.Toggle("coating", "x", true);
        s.Toggle("coating", "x", false);
        s.SetRange("diameter", "6", " ");
        s.SetRange("length", "", "");
        s.SetFlag("critical", true);
        var filters = s.ToFilters();
        Assert.Equal(3, filters.Count);
        Assert.Equal(["304", "316"], filters.Single(f => f.Attribute == "grade").AnyOf);
        Assert.Equal((6m, null), (filters.Single(f => f.Attribute == "diameter").Min, filters.Single(f => f.Attribute == "diameter").Max));
        Assert.True(filters.Single(f => f.Attribute == "critical").Flag);
        s.Clear();
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Numbers_are_formatted_with_prefix_and_width()
    {
        Assert.Equal("P-000042", NumberingAdminService.Format("P-", 6, 42));
        Assert.Equal("PRT-0000001", NumberingAdminService.Format("PRT-", 7, 1));
    }

    [Fact]
    public void Template_drafts_round_trip_and_diffs_name_the_changed_nodes()
    {
        const string json = """
        {"id":"t","name":"T","objectType":"part","slots":[{"name":"r","label":"R","role":"plm-viewer","min":1,"max":1}],"start":"flow",
         "nodes":[{"id":"flow","type":"Serial","name":"Flow","steps":["a","end"]},{"id":"a","type":"Task","name":"A","kind":"review","assignment":{"mode":"Slot","slot":"r"},"dueDays":3},{"id":"end","type":"End","name":"End","outcome":"Release"}]}
        """;
        var (draft, error) = TemplateDraft.FromJson(json);
        Assert.Null(error);
        Assert.Equal(CanonicalJson.Serialize(JsonNode.Parse(json)), CanonicalJson.Serialize(draft!.ToJson()));
        draft.Nodes.Single(n => n.Id == "a").DueDays = 7;
        draft.Nodes.Add(new NodeDraft { Id = "b", Name = "B" });
        var lines = TemplateDiff.Compare(JsonNode.Parse(json)!, draft.ToJson());
        Assert.Contains("Changed node 'a', dueDays: 3 → 7.", lines);
        Assert.Contains("Added node 'b'.", lines);
        Assert.Empty(TemplateDiff.Compare(JsonNode.Parse(json)!, JsonNode.Parse(json)!));
    }

    [Fact]
    public void Canonical_json_sorts_keys_for_stable_hashes()
    {
        Assert.Equal("""{"a":1,"b":{"c":2,"d":3}}""", CanonicalJson.Serialize(JsonNode.Parse("""{"b":{"d":3,"c":2},"a":1}""")));
        Assert.Equal(ConfigDocument.HashOf(Empty()), ConfigDocument.HashOf(Empty()));
        Assert.NotEqual(ConfigDocument.HashOf(Empty()), ConfigDocument.HashOf(Empty() with { Numbering = [new NumberingDoc("part", "X-", 6)] }));
    }

    private static ConfigContent Empty() => new(new Dictionary<string, IReadOnlyList<AttributeDoc>>(), [], new Dictionary<string, IReadOnlyList<RefItemDoc>>(), [], [], [], [],
        new ClassHierarchyDoc(ClassHierarchyDoc.CurrentFormat, []));
}
