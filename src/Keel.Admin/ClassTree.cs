namespace Keel.Admin;

public sealed record ClassNodeDto(Guid Id, string Code, string Name, string Description, Guid? ParentId, string Path, int Depth, int SortOrder, string Status)
{
    public bool IsActive => Status == "active";
}

/// <summary>An attribute of a class's effective set, with the class that defines it (inheritance: ancestors first).</summary>
public sealed record EffectiveAttribute(AttributeDefinitionDto Definition, string DefinedAt);

public sealed record ClassNodeInput(string Code, string Name, string? Description, string? ParentCode, int SortOrder = 0);

public sealed record ClassDetail(ClassNodeDto Node, IReadOnlyList<ClassNodeDto> Ancestors, IReadOnlyList<ClassNodeDto> Children, IReadOnlyList<EffectiveAttribute> Effective,
    IReadOnlyList<AttributeDefinitionDto> OwnAttributes, long ClassifiedParts);

/// <summary>Existing classified objects that would no longer satisfy a changed class definition, as an administrator sees them before confirming.</summary>
public sealed record ClassImpact(long Conflicts, IReadOnlyList<string> Messages, IReadOnlyList<string> ExampleParts)
{
    public static readonly ClassImpact None = new(0, [], []);
    public bool Blocks => Conflicts > 0;
}

/// <summary>
/// The classification hierarchy in memory with attribute inheritance (ADR-0022): the effective attribute set of a class is the attributes of its
/// ancestors, root first, followed by its own. Pure and immutable; all rules about names, depth and subtrees live here.
/// </summary>
public sealed class ClassTree
{
    public const int MaxDepth = 12;

    private readonly Dictionary<Guid, ClassNodeDto> _byId;
    private readonly Dictionary<string, ClassNodeDto> _byCode;
    private readonly Dictionary<Guid, List<AttributeDefinitionDto>> _attributes;

    public ClassTree(IEnumerable<ClassNodeDto> nodes, IEnumerable<AttributeDefinitionDto> attributes)
    {
        _byId = nodes.ToDictionary(n => n.Id);
        _byCode = _byId.Values.ToDictionary(n => n.Code, StringComparer.OrdinalIgnoreCase);
        _attributes = attributes.GroupBy(a => Guid.Parse(a.Owner)).ToDictionary(g => g.Key, g => g.OrderBy(a => a.SortOrder).ThenBy(a => a.Name, StringComparer.Ordinal).ToList());
    }

    public IReadOnlyCollection<ClassNodeDto> Nodes => _byId.Values;

    public ClassNodeDto? ById(Guid id) => _byId.GetValueOrDefault(id);

    public ClassNodeDto? ByCode(string? code) => code is null ? null : _byCode.GetValueOrDefault(code);

    public IReadOnlyList<ClassNodeDto> Children(Guid? parentId) =>
        [.. _byId.Values.Where(n => n.ParentId == parentId).OrderBy(n => n.SortOrder).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Root-first chain including the node itself.</summary>
    public IReadOnlyList<ClassNodeDto> Chain(Guid id)
    {
        var chain = new List<ClassNodeDto>();
        for (var n = ById(id); n is not null; n = n.ParentId is { } p ? ById(p) : null) chain.Insert(0, n);
        return chain;
    }

    /// <summary>The node and every descendant.</summary>
    public IReadOnlyList<ClassNodeDto> Subtree(Guid id)
    {
        var root = ById(id);
        return root is null ? [] : [.. _byId.Values.Where(n => n.Path.StartsWith(root.Path, StringComparison.Ordinal)).OrderBy(n => n.Path, StringComparer.Ordinal)];
    }

    public IReadOnlyList<AttributeDefinitionDto> OwnAttributes(Guid id) => _attributes.GetValueOrDefault(id) ?? [];

    /// <summary>Ancestors' attributes (root first), then the node's own. Retired attributes are included, flagged.</summary>
    public IReadOnlyList<EffectiveAttribute> Effective(Guid id) =>
        [.. Chain(id).SelectMany(n => OwnAttributes(n.Id).Select(a => new EffectiveAttribute(a, n.Code)))];

    public IReadOnlyList<Keel.Core.AttributeSpec> EffectiveSpecs(Guid id) => [.. Effective(id).Select(e => e.Definition.ToSpec())];

    /// <summary>The height of the subtree below a node (0 for a leaf), to check the depth limit before a move.</summary>
    public int Height(Guid id)
    {
        var root = ById(id);
        return root is null ? 0 : Subtree(id).Max(n => n.Depth) - root.Depth;
    }

    /// <summary>
    /// Attribute names must be unique along every root-to-leaf path, or an effective set would be ambiguous. Returns a message per clash
    /// when <paramref name="node"/> (with its attributes) sits under <paramref name="newParent"/>.
    /// </summary>
    public List<string> NameClashesWhenPlacedUnder(Guid node, Guid? newParent)
    {
        var clashes = new List<string>();
        if (newParent is null) return clashes;
        var above = Chain(newParent.Value).SelectMany(n => OwnAttributes(n.Id).Select(a => (n.Code, a.Name))).ToList();
        foreach (var inSubtree in Subtree(node))
            foreach (var a in OwnAttributes(inSubtree.Id))
                foreach (var (code, name) in above.Where(x => x.Name == a.Name))
                    clashes.Add($"Attribute '{a.Name}' of {inSubtree.Code} is also defined by {code}, which would become an ancestor.");
        return clashes;
    }

    /// <summary>A new attribute name must not exist on the node, any ancestor, or any descendant.</summary>
    public string? NameClash(Guid node, string name)
    {
        foreach (var n in Chain(node).Concat(Subtree(node)).DistinctBy(n => n.Id))
            if (OwnAttributes(n.Id).Any(a => a.Name == name))
                return n.Id == node ? $"The class already defines '{name}'."
                    : n.Path.StartsWith(ById(node)!.Path, StringComparison.Ordinal) ? $"'{name}' is already defined by the subclass {n.Code}."
                    : $"'{name}' is already defined by the ancestor {n.Code}.";
        return null;
    }

    public static string PathOf(IEnumerable<ClassNodeDto> chain) => "/" + string.Join('/', chain.Select(n => n.Code)) + "/";
}
