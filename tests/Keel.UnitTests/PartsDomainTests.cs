using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Parts;

namespace Keel.UnitTests;

public sealed class RevisionSchemeTests
{
    [Theory]
    [InlineData("A", "B")]
    [InlineData("H", "J")]   // skips I
    [InlineData("N", "P")]   // skips O
    [InlineData("P", "R")]   // skips Q
    [InlineData("R", "T")]   // skips S
    [InlineData("W", "Y")]   // skips X
    [InlineData("Y", "AA")]  // Z never used; rolls over
    [InlineData("AY", "BA")]
    [InlineData("YY", "AAA")]
    public void Next_follows_asme_y14_35(string current, string next) => Assert.Equal(next, RevisionScheme.Next(current));

    [Theory]
    [InlineData("I")]
    [InlineData("O")]
    [InlineData("Z")]
    [InlineData("a")]
    [InlineData("")]
    [InlineData("AAAA")]
    public void Invalid_labels_are_rejected(string label)
    {
        Assert.False(RevisionScheme.IsValid(label));
        Assert.Throws<ArgumentException>(() => RevisionScheme.Next(label));
    }

    [Fact]
    public void Index_round_trips_and_orders()
    {
        for (var i = 1; i < 9000; i++) Assert.Equal(i, RevisionScheme.ToIndex(RevisionScheme.FromIndex(i)));
        Assert.True(RevisionScheme.ToIndex("Y") < RevisionScheme.ToIndex("AA"));
    }
}

public sealed class AttributeValidatorTests
{
    private static readonly AttributeDefinition[] Defs =
    [
        new("material", "Material", "string", null, null, false, 10, 1),
        new("mass", "Mass", "number", "kg", null, false, null, 2),
        new("critical_item", "Critical item", "boolean", null, null, false, null, 3),
        new("criticality", "Criticality", "enum", null, ["Standard", "Major"], false, null, 4),
        new("cage_code", "CAGE code", "string", null, null, true, 5, 5),
    ];

    [Fact]
    public void Valid_values_are_normalized_and_key_ordered()
    {
        var (n, errors) = AttributeValidator.Validate(Defs, new JsonObject
        {
            ["mass"] = "1.25", ["material"] = "  Ti-6Al-4V ", ["critical_item"] = true, ["criticality"] = "Major", ["cage_code"] = "1ABC2",
        });
        Assert.Empty(errors);
        Assert.Equal("""{"cage_code":"1ABC2","critical_item":true,"criticality":"Major","mass":1.25,"material":"Ti-6Al-4V"}""", n.ToJsonString());
    }

    [Fact]
    public void Every_error_is_reported()
    {
        var (_, errors) = AttributeValidator.Validate(Defs, new JsonObject
        {
            ["mass"] = "heavy", ["material"] = "much too long text", ["critical_item"] = "yes", ["criticality"] = "Minor", ["colour"] = "red",
        });
        Assert.Equal(
        [
            "Mass must be a number.", "Material must be at most 10 characters.", "Critical item must be true or false.",
            "Criticality must be one of: Standard, Major.", "Unknown attribute 'colour'.", "CAGE code is required.",
        ], errors);
    }

    [Fact]
    public void Blank_values_mean_not_set()
    {
        var (n, errors) = AttributeValidator.Validate(Defs, new JsonObject { ["material"] = " ", ["cage_code"] = "12345" });
        Assert.Empty(errors);
        Assert.False(n.ContainsKey("material"));
    }
}

public sealed class AggregateBannerTests
{
    [Fact]
    public void Aggregates_most_restrictive_markings()
    {
        var itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
            controllingOffice: "ACME", distributionReason: "x").Value;
        var ear = Markings.Create(ExportJurisdiction.Ear, DistributionStatement.F, "ENG", eccn: "9E610", cuiCategories: ["EXPT", "PROPIN"],
            controllingOffice: "ACME", distributionReason: "x", program: "ORCA").Value;
        var open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

        Assert.Equal("CUI//EXPT/PROPIN · ITAR · EAR 9E610 · DISTRIBUTION F", Markings.AggregateBanner([open, itar, ear]));
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", Markings.AggregateBanner([open]));
        Assert.Equal("UNCLASSIFIED", Markings.AggregateBanner([]));
    }
}
